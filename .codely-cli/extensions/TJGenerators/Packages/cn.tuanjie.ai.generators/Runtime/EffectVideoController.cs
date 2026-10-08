using UnityEngine;
using UnityEngine.Video;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// [ExecuteAlways] controller that manages RenderTexture lifecycle for
    /// ChromaKey effect video playback. Survives domain reloads by recreating
    /// the RT and restarting playback in OnEnable.
    /// </summary>
    [ExecuteAlways]
    public class EffectVideoController : MonoBehaviour
    {
        [SerializeField] private Material chromaKeyMaterial;

        private VideoPlayer _player;
        private Renderer _renderer;
        private RenderTexture _rt;
        private MaterialPropertyBlock _mpb;
        private bool _needPlay;

        public void Initialize(Material mat)
        {
            chromaKeyMaterial = mat;
            SetupPlayback();
        }

        private void OnEnable()
        {
            SetupPlayback();
        }

        private void SetupPlayback()
        {
            _player = GetComponent<VideoPlayer>();
            _renderer = GetComponent<Renderer>();

            if (_player == null || _renderer == null || chromaKeyMaterial == null)
                return;

            // Ensure the ChromaKey material is assigned
            _renderer.sharedMaterial = chromaKeyMaterial;

            // Create or recreate RT
            int w = _player.width > 0 ? (int)_player.width : 1280;
            int h = _player.height > 0 ? (int)_player.height : 720;
            if (_rt == null || !_rt.IsCreated())
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                _rt.Create();
            }

            _player.targetTexture = _rt;

            // Use MaterialPropertyBlock so the material asset is never modified
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetTexture("_MainTex", _rt);
            _renderer.SetPropertyBlock(_mpb);

            // Start playback — if already prepared, play immediately;
            // otherwise flag for Update() to start once prepared
            if (_player.isPrepared)
            {
                if (!_player.isPlaying)
                    _player.Play();
            }
            else
            {
                _needPlay = true;
                _player.Prepare();
            }
        }

        private void Update()
        {
            if (_needPlay && _player != null && _player.isPrepared)
            {
                _needPlay = false;
                if (!_player.isPlaying)
                    _player.Play();
            }
        }

        private void OnDisable()
        {
            _needPlay = false;

            if (_rt != null)
            {
                _rt.Release();
                if (Application.isPlaying)
                    Destroy(_rt);
                else
                    DestroyImmediate(_rt);
                _rt = null;
            }
            if (_mpb != null && _renderer != null)
            {
                _mpb.Clear();
                _renderer.SetPropertyBlock(_mpb);
            }
        }
    }
}
