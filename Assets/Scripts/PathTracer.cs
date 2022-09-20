using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum SamplesPerPixel
{
    One = 1,
    Two = 2,
    Four = 4,
    Eight = 8,
    Sixteen = 16
}

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    [Range(1,16)] public int downscaleFactor = 1;
    [Range(0.0f,1.0f)] public float blendAmount;
    public SamplesPerPixel samplesPerPixel;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _computeShader;
        private readonly int _downscaleFactor;
        private readonly float _blendAmount;
        private float _verticalFov;
        private Vector3 _cameraPosition, _cameraDirection;
        private readonly int _samplesPerPixel;
        public PathTracerPass(int downscaleFactor, float blendAmount, int samplesPerPixel)
        {
            _blendAmount = blendAmount;
            _samplesPerPixel = samplesPerPixel;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _computeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _verticalFov = renderingData.cameraData.camera.fieldOfView;
            _cameraPosition = renderingData.cameraData.camera.transform.position;
            _cameraDirection = -renderingData.cameraData.camera.transform.forward;

            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            //_verticalFov = SceneView.currentDrawingSceneView.camera.fieldOfView;
            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var _sceneTexture = RenderTexture.GetTemporary(descriptor);
            var _upscaleTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            var _outputTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(ProfilerTag);
            cmd.Blit(_colorBuffer,_sceneTexture);
            
            // Execute compute
            _computeShader.SetInt("Width", _downscaleTexture.width);
            _computeShader.SetInt("Height", _downscaleTexture.height);
            _computeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _computeShader.SetInt("SamplesPerPixel", _samplesPerPixel);
            _computeShader.SetFloat("BlendAmount", _blendAmount);
            _computeShader.SetFloat("VerticalFov", _verticalFov);
            _computeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
            _computeShader.SetVector("CameraPosition", new Vector4(_cameraPosition.x, _cameraPosition.y, _cameraPosition.z, 0));
            
            _computeShader.SetTexture(0, "Result", _outputTexture);
            _computeShader.SetTexture(0, "Downscale", _downscaleTexture);
            _computeShader.SetTexture(0, "Scene", _sceneTexture);
            _computeShader.Dispatch(0, _downscaleTexture.width, _downscaleTexture.height, 1);
            
            var request = AsyncGPUReadback.Request(_outputTexture);
            request.WaitForCompletion();
            
            cmd.Blit(_downscaleTexture,_upscaleTexture);

            _computeShader.SetTexture(1, "Result", _outputTexture);
            _computeShader.SetTexture(1, "Upscale", _upscaleTexture);
            _computeShader.SetTexture(1, "Scene", _sceneTexture);
            _computeShader.Dispatch(1, _sceneTexture.width, _sceneTexture.height, 1);
            
            // Sync compute with frame
            request = AsyncGPUReadback.Request(_outputTexture);
            request.WaitForCompletion();
            
            // Clean up
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_sceneTexture);
            RenderTexture.ReleaseTemporary(_upscaleTexture);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
            
            // Copy processed texture into scene buffer
            cmd.Blit(_outputTexture,_colorBuffer);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, blendAmount, (int)samplesPerPixel);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
