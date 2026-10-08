using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// <c>manage_dialog</c>: clicks a button on a native modal editor dialog.
    ///
    /// Single action:
    ///   click – Params: button (regex over button labels), optional title (regex over dialog
    ///           titles). Clicks the first open modal dialog whose title/button match.
    ///
    /// The command runs on the <see cref="BackgroundCommandPump"/> worker thread, which is the
    /// point: a native modal dialog holds the editor main thread captive inside its message
    /// pump, so this is the only way a client can answer one. The click itself is thread-safe
    /// on Windows (Win32 cross-thread post); on macOS <see cref="DialogWatcher"/> marshals
    /// it onto the common-modes run-loop probe, which keeps firing on the main thread even
    /// during a modal session.
    ///
    /// When such a dialog is detected, <see cref="DialogWatcher"/> detaches every
    /// in-flight job with a reason that names the dialog and its buttons and points at this
    /// command — the client picks a button, clicks it here, and then collects the detached
    /// jobs' results via <c>manage_job.status</c>.
    /// </summary>
    public static class ManageDialog
    {
        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "click", Click },
            };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers);

        private static object Click(JObject @params)
        {
            string button = @params["button"]?.ToString();
            string title = @params["title"]?.ToString();

            string failure = DialogWatcher.ClickOpenDialog(title, button,
                out string clickedTitle, out string clickedButton);
            return failure != null
                ? Response.Error(failure)
                : Response.Success($"Clicked '{clickedButton}' on dialog '{clickedTitle}'.");
        }
    }
}
