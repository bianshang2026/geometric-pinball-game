using System;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// One step of a <see cref="StepJob"/>: the work to perform and the condition for leaving it.
    ///
    /// <see cref="StepJobRunner"/> runs at most one step per editor frame. Each frame it invokes
    /// <see cref="Run"/> and then evaluates <see cref="CanLeave"/>; while the condition is false the
    /// same step runs again next frame, so <see cref="Run"/> must be safe to repeat. A null
    /// <see cref="CanLeave"/> means the step leaves after a single frame.
    ///
    /// <see cref="Skip"/> is checked first, and a skipped step costs nothing: the runner moves to
    /// the next step and keeps going in the same frame, so a run of skipped steps is passed over in
    /// one tick and the first step that does not skip runs in that same tick.
    ///
    /// A step built with <see cref="Nested"/> instead runs a whole child <see cref="StepJob"/> to
    /// completion before it may be left — see that method for the ordering.
    ///
    /// Steps are rebuilt from <see cref="StepJob.BuildSteps"/> after a domain reload, so their
    /// delegates may capture the job instance but must not hold state that has to survive the
    /// reload — that state belongs in serialized fields on the <see cref="StepJob"/> subclass.
    /// </summary>
    public sealed class JobStep
    {
        /// <summary>Human-readable step name, reported in timeout and progress messages.</summary>
        public string Name { get; }

        /// <summary>Work for this step; invoked once per frame until <see cref="CanLeave"/> holds.</summary>
        public Action Run { get; }

        /// <summary>Leave condition. Null means leave after one frame.</summary>
        public Func<bool> CanLeave { get; }

        /// <summary>
        /// Skip condition, re-evaluated at the top of every frame before <see cref="Run"/>. When it
        /// holds, the step is passed over — its <see cref="Run"/> and, for a composite step, its
        /// child never execute — and the next step is taken up in the same frame.
        ///
        /// Because it is re-evaluated each frame, a step that is already in progress (its
        /// <see cref="CanLeave"/> still false) is abandoned if its skip condition becomes true.
        /// </summary>
        public Func<bool> Skip { get; }

        /// <summary>Child-job factory for a composite step; null for a plain step.</summary>
        public Func<StepJob> SubJob { get; }

        /// <summary>Invoked once when this step's child job finishes, before <see cref="CanLeave"/>.</summary>
        public Action OnSubJobComplete { get; }

        public JobStep(string name, Action run, Func<bool> canLeave = null, Func<bool> skip = null)
        {
            Name = string.IsNullOrEmpty(name) ? "step" : name;
            Run = run;
            CanLeave = canLeave;
            Skip = skip;
        }

        private JobStep(string name, Func<StepJob> subJob, Action onSubJobComplete,
                        Func<bool> canLeave, Func<bool> skip)
        {
            Name = string.IsNullOrEmpty(name) ? "sub-job" : name;
            SubJob = subJob;
            OnSubJobComplete = onSubJobComplete;
            CanLeave = canLeave;
            Skip = skip;
        }

        /// <summary>
        /// A composite step: runs <paramref name="subJob"/> — itself a full <see cref="StepJob"/>
        /// with its own steps, which may nest further — to completion, then invokes
        /// <paramref name="onComplete"/> once and evaluates <paramref name="canLeave"/>.
        ///
        /// The child's outcome is available to the parent as
        /// <see cref="StepJob.LastSubJobResponse"/> / <see cref="StepJob.LastSubJobSucceeded"/>
        /// from <paramref name="onComplete"/> onwards; both survive a domain reload. A non-null
        /// <paramref name="canLeave"/> that is still false after the child finishes keeps the
        /// parent on this step (the child is not restarted).
        ///
        /// <paramref name="skip"/> passes over the whole step — child included — for free, in the
        /// same frame. Alternatively <paramref name="subJob"/> may return null to skip only the
        /// child while still honouring <paramref name="canLeave"/>; use <paramref name="skip"/>
        /// when the step should cost nothing, and the null factory when the parent still wants to
        /// wait on this step after deciding against the child. Either way
        /// <paramref name="onComplete"/> does not fire.
        /// </summary>
        public static JobStep Nested(string name, Func<StepJob> subJob, Action onComplete = null,
                                     Func<bool> canLeave = null, Func<bool> skip = null)
        {
            if (subJob == null) throw new ArgumentNullException(nameof(subJob));
            return new JobStep(name, subJob, onComplete, canLeave, skip);
        }
    }

    /// <summary>
    /// A multi-step job that survives domain reloads. Subclass it, declare the steps in
    /// <see cref="BuildSteps"/>, and keep every piece of state the job needs across a reload in
    /// public (or <c>[JsonProperty]</c>-attributed) fields — the runner serializes the instance to
    /// SessionState and rebuilds it from that JSON in the new domain.
    ///
    /// The subclass must have a parameterless constructor and must not depend on anything captured
    /// at construction time that cannot be reconstructed, since only the serialized fields and the
    /// current step index carry over.
    ///
    /// Jobs compose: a step created with <see cref="JobStep.Nested"/> runs another StepJob to
    /// completion before continuing, to any depth up to
    /// <see cref="StepJobRunner.MaxNestingDepth"/>. The whole nest is persisted, so a child job
    /// keeps its own progress across a reload just as the parent does.
    ///
    /// Report the outcome with <see cref="Complete"/> or <see cref="Fail"/>. Falling off the end of
    /// the last step completes the job with a synthesized success response.
    /// </summary>
    public abstract class StepJob
    {
        [JsonIgnore] private JobStep[] _steps;

        /// <summary>The job's steps, built lazily and rebuilt after each domain reload.</summary>
        [JsonIgnore]
        public JobStep[] Steps => _steps ?? (_steps = BuildSteps() ?? Array.Empty<JobStep>());

        /// <summary>Declares the ordered steps. Called once per domain, after deserialization.</summary>
        protected abstract JobStep[] BuildSteps();

        /// <summary>
        /// Hook invoked after the job has been restored from SessionState in a new domain, before
        /// its next step runs. Use it to re-acquire anything that could not be serialized.
        /// </summary>
        public virtual void OnRestored() { }

        // Outcome is kept as serialized JSON (not an object graph) so it survives the reload
        // round-trip unchanged, exactly as it will be handed to the client.
        [JsonProperty("__outcomeJson")]  private string _outcomeJson;
        [JsonProperty("__failed")]       private bool   _failed;
        [JsonProperty("__finished")]     private bool   _finished;
        [JsonProperty("__subJobResult")] private string _subJobResponseJson;

        [JsonIgnore] private JObject _subJobResponseCache;

        /// <summary>True once <see cref="Complete"/> or <see cref="Fail"/> has been called.</summary>
        [JsonIgnore] internal bool IsFinished => _finished;

        /// <summary>True when the job ended via <see cref="Fail"/>.</summary>
        [JsonIgnore] public bool HasFailed => _failed;

        /// <summary>
        /// Response of the most recently completed nested job, or null if none has run. Set by the
        /// runner just before a composite step's <c>onComplete</c> hook, and persisted, so it is
        /// still readable after a domain reload that lands mid-step.
        /// </summary>
        [JsonIgnore]
        protected JObject LastSubJobResponse
        {
            get
            {
                if (_subJobResponseCache != null) return _subJobResponseCache;
                if (string.IsNullOrEmpty(_subJobResponseJson)) return null;
                try { _subJobResponseCache = JObject.Parse(_subJobResponseJson); }
                catch { _subJobResponseCache = null; }
                return _subJobResponseCache;
            }
        }

        /// <summary>True when the last nested job reported success. False when none has run.</summary>
        [JsonIgnore]
        protected bool LastSubJobSucceeded => LastSubJobResponse?["success"]?.ToObject<bool?>() == true;

        /// <summary>Raw response JSON of the last nested job, for forwarding verbatim.</summary>
        [JsonIgnore]
        protected internal string LastSubJobResponseJson => _subJobResponseJson;

        /// <summary>Runner hook: records a finished child job's response on its parent.</summary>
        internal void SetSubJobResponse(string responseJson)
        {
            _subJobResponseJson = responseJson;
            _subJobResponseCache = null;
        }

        /// <summary>
        /// Ends the job successfully. Pass a <see cref="Response"/> payload, or null to let the
        /// runner synthesize a success response from the job name.
        /// </summary>
        public void Complete(object response = null)
        {
            _failed = false;
            _outcomeJson = Serialize(response);
            _finished = true;
        }

        /// <summary>
        /// Ends the job with an error. Pass a message (wrapped in <see cref="Response.Error"/>), a
        /// ready-made <see cref="Response"/> payload via <see cref="FailWith"/>, or null to let the
        /// runner synthesize an error response from the job name.
        /// </summary>
        public void Fail(string message = null)
            => FailWith(message == null ? null : Response.Error(message));

        /// <summary>Ends the job with an already-built error response payload.</summary>
        public void FailWith(object response)
        {
            _failed = true;
            _outcomeJson = Serialize(response);
            _finished = true;
        }

        /// <summary>Ends the job with an already-serialized response, kept verbatim.</summary>
        public void CompleteWithJson(string responseJson, bool failed = false)
        {
            _failed = failed;
            _outcomeJson = responseJson;
            _finished = true;
        }

        /// <summary>Builds the response JSON the runner delivers to the client.</summary>
        internal string ToResponseJson(string name, string jobId)
        {
            if (!string.IsNullOrEmpty(_outcomeJson)) return _outcomeJson;

            var jobData = new { job = name, id = jobId };
            object response = _failed
                ? Response.Error(name ?? "Operation failed.", jobData)
                : Response.Success(name ?? "Operation completed.", jobData);
            return JsonConvert.SerializeObject(response);
        }

        private static string Serialize(object response)
            => response == null ? null : JsonConvert.SerializeObject(response);
    }

    /// <summary>
    /// The "has it started yet?" window used by steps that request something asynchronous from the
    /// editor — a compile, a lighting bake — and must not conclude "it never started" while the
    /// editor is still busy with the asset refresh the work comes out of.
    ///
    /// The deadline is kept out of the way for as long as the editor reports
    /// <c>isUpdating</c>: every frame spent refreshing pushes it forward again, so the window only
    /// ever counts down against an editor that is idle and still not doing the work. Merely
    /// suppressing the leave condition during the refresh is not enough — the clock would run out
    /// underneath it, and the first idle frame after a long refresh would leave the step before the
    /// compile it was waiting for had a chance to start.
    ///
    /// The deadline is held in a serialized field on the job so it survives a domain reload; these
    /// two methods are the whole rule and are static so they can be tested without an editor.
    /// </summary>
    public static class SettleWindow
    {
        /// <summary>
        /// The deadline to store this frame: <paramref name="now"/> plus
        /// <paramref name="seconds"/> on the first frame and for as long as the editor is updating,
        /// otherwise the deadline already set.
        /// </summary>
        public static double Advance(double until, double now, double seconds, bool isUpdating)
            => until <= 0 || isUpdating ? now + seconds : until;

        /// <summary>True when the window has run out on an editor that is not updating.</summary>
        public static bool Expired(double until, double now, bool isUpdating)
            => !isUpdating && until > 0 && now >= until;
    }
}
