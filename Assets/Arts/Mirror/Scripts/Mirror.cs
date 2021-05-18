using UnityEngine;
using System.Collections;
using System;
using System.Collections.Generic;
using UnityEngine.Rendering.Universal;
using RenderPipeline = UnityEngine.Rendering.RenderPipelineManager;
using UnityEngine.Rendering;

public class Mirror : MonoBehaviour
{
    public string ReflectionSample = "_ReflectionTex";
    public static bool s_InsideRendering = false;
    private int uniqueTextureID = -1;

    [HideInInspector]
    public int textureSize
    {
        get
        {
            return m_TextureSize;
        }
        set
        {
            if (!Application.isPlaying)
            {
                m_TextureSize = Mathf.Clamp(value, 1, 2048);
            }
        }
    }

    [HideInInspector]
    public int m_TextureSize = 256;
    [HideInInspector]
    public float m_ClipPlaneOffset = 0.01f;
    [Tooltip("With lots of small mirrors in the same plane position, you can add several SmallMirrors components and manage them with only one Mirror component to significantly save cost")]
    public SmallMirrors[] allMirrors = new SmallMirrors[0];

    public enum AntiAlias
    {
        X1 = 1,
        X2 = 2,
        X4 = 4,
        X8 = 8
    }
    [Tooltip("The normal transform(transform.up as normal)")]
    public Transform NormalTrans;

    public enum RenderQuality
    {
        Default,
        High,
        Medium,
        Low,
        VeryLow
    }

    private RenderTexture m_ReflectionTexture = null;
    [HideInInspector]
    public bool useDistanceCull = false;
    [HideInInspector] public float m_SqrMaxdistance = 2500f;
    [HideInInspector] public float m_maxDistance = 50f;

    public float maxDistance
    {
        get
        {
            return m_maxDistance;
        }
        set
        {
            m_maxDistance = value;
            m_SqrMaxdistance = value * value;
        }
    }

    [HideInInspector]
    public float[] layerCullingDistances = new float[32];
    [HideInInspector]
    public Renderer render;
    private Camera _currentCamera;
    private Camera _reflectionCamera;
    private Transform refT;
    private Transform camT;
    private List<Material> allMats = new List<Material>();

    private bool billboard;
    private bool softVeg;
    private bool softParticle;
    private AnisotropicFiltering ani;
    private UnityEngine.ShadowResolution shaR;
    private UnityEngine.ShadowQuality shadowQuality;

    private float widthHeightRate;

    [Tooltip("MSAA anti alias")]
    public AntiAlias MSAA = AntiAlias.X8;

    private Camera depthCam;
    private RenderTexture depthTexture;
    //private DepthOfField dof;

    [Header("Optimization & Culling")]
    [Tooltip("Reflection Quality")]
    public RenderQuality renderQuality = RenderQuality.Default;
    [Tooltip("Mirror mask")]
    public LayerMask m_ReflectLayers = -1;
    public bool enableSelfCullingDistance = true;
    void Awake()
    {
        uniqueTextureID = Shader.PropertyToID(ReflectionSample);
        if (!NormalTrans)
        {
            NormalTrans = new GameObject("Normal Trans").transform;
            NormalTrans.position = transform.position;
            NormalTrans.rotation = transform.rotation;
            NormalTrans.SetParent(transform);
        }
        render = GetComponent<Renderer>();
        if (!render || !render.sharedMaterial)
        {
            Destroy(this);
        }
        for (int i = 0; i < allMirrors.Length; ++i)
        {
            allMirrors[i].manager = this;
        }
        for (int i = 0, length = render.sharedMaterials.Length; i < length; ++i)
        {
            Material m = render.sharedMaterials[i];
            if (!allMats.Contains(m))
                allMats.Add(m);
        }
        for (int i = 0; i < allMirrors.Length; ++i)
        {
            Renderer r = allMirrors[i].GetRenderer();
            for (int a = 0, length = r.sharedMaterials.Length; a < length; ++a)
            {
                Material m = r.sharedMaterials[a];
                if (!allMats.Contains(m))
                    allMats.Add(m);
            }
        }

        RenderPipeline.beginCameraRendering += UpdateRefelctionCamera;

        m_SqrMaxdistance = m_maxDistance * m_maxDistance;
        widthHeightRate = (float)Screen.height / (float)Screen.width;
        m_ReflectionTexture = new RenderTexture(m_TextureSize, (int)(m_TextureSize * widthHeightRate + 0.5), 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Default);
        m_ReflectionTexture.name = "ReflectionTex " + GetInstanceID();
        m_ReflectionTexture.isPowerOfTwo = true;
        m_ReflectionTexture.filterMode = FilterMode.Trilinear;
        m_ReflectionTexture.antiAliasing = (int)MSAA;
        GameObject go = new GameObject("MirrorCam", typeof(Camera), typeof(FlareLayer));
        //go.hideFlags = HideFlags.HideAndDontSave;
        _reflectionCamera = go.GetComponent<Camera>();
        //PostProcessLayer postProcessLayer = go.GetComponent<PostProcessLayer>();
        //postProcessLayer.volumeLayer = 1 << normalTrans.gameObject.layer;
        //mysky = go.AddComponent<Skybox> ();
        go.transform.SetParent(NormalTrans);
        go.transform.localPosition = Vector3.zero;
        _reflectionCamera.enabled = false;
        _reflectionCamera.targetTexture = m_ReflectionTexture;
        _reflectionCamera.cullingMask = ~(1 << 4) & m_ReflectLayers.value;
        _reflectionCamera.layerCullSpherical = enableSelfCullingDistance;
        refT = _reflectionCamera.transform;
        if (!enableSelfCullingDistance)
        {
            for (int i = 0, length = layerCullingDistances.Length; i < length; ++i)
            {
                layerCullingDistances[i] = 0;
            }
        }
        else
        {
            _reflectionCamera.layerCullDistances = layerCullingDistances;
        }
        _reflectionCamera.useOcclusionCulling = false;       //Custom Projection Camera should not use occlusionCulling!
        SetTexture(m_ReflectionTexture);
    }

    private void OnEnable()
    {
        SetTexture(m_ReflectionTexture);
    }

    private void OnDestroy()
    {
        RenderPipeline.beginCameraRendering -= UpdateRefelctionCamera;
    }

    void UpdateRefelctionCamera(ScriptableRenderContext SRC, Camera camera)
    {
        _currentCamera = Camera.main;
        camT = _currentCamera.transform;
        _reflectionCamera.fieldOfView = _currentCamera.fieldOfView;
        _reflectionCamera.aspect = _currentCamera.aspect;

        if (useDistanceCull && Vector3.SqrMagnitude(NormalTrans.position - camT.position) > m_SqrMaxdistance)
        {
            s_InsideRendering = false;
            return;
        }

        Vector3 localPos = NormalTrans.worldToLocalMatrix.MultiplyPoint3x4(camT.position);
        if (localPos.y < 0)
        {
            s_InsideRendering = false;
            return;
        }

        refT.eulerAngles = camT.eulerAngles;
        Vector3 localEuler = refT.localEulerAngles;
        localEuler.x *= -1;
        localEuler.z *= -1;
        localPos.y *= -1;
        refT.localEulerAngles = localEuler;

        refT.localPosition = localPos;

        // Vector3 normal = NormalTrans.up;
        // Vector3 pos = NormalTrans.position;
        float d = -Vector3.Dot(NormalTrans.up, NormalTrans.position) - m_ClipPlaneOffset;
        Vector4 reflectionPlane = new Vector4(NormalTrans.up.x, NormalTrans.up.y, NormalTrans.up.z, d);
        CalculateReflectionMatrix(ref reflection, ref reflectionPlane);
        ref_WorldToCam = _currentCamera.worldToCameraMatrix * reflection;
        _reflectionCamera.worldToCameraMatrix = ref_WorldToCam;
        Vector4 clipPlane = CameraSpacePlane(ref ref_WorldToCam, NormalTrans.position, NormalTrans.up);
        _reflectionCamera.projectionMatrix = _currentCamera.CalculateObliqueMatrix(clipPlane);
        GL.invertCulling = true;

#if UNITY_EDITOR
        if (renderQuality == RenderQuality.VeryLow)
        {
            if (_reflectionCamera.renderingPath != RenderingPath.VertexLit)
                _reflectionCamera.renderingPath = RenderingPath.VertexLit;
        }
        else if (_reflectionCamera.renderingPath != _currentCamera.renderingPath)
        {
            _reflectionCamera.renderingPath = _currentCamera.renderingPath;
        }
#endif

        switch (renderQuality)
        {
            case RenderQuality.Default:
                UniversalRenderPipeline.RenderSingleCamera(SRC, _reflectionCamera);
                break;
            case RenderQuality.High:
                billboard = QualitySettings.billboardsFaceCameraPosition;
                QualitySettings.billboardsFaceCameraPosition = false;
                softParticle = QualitySettings.softParticles;
                softVeg = QualitySettings.softVegetation;
                QualitySettings.softParticles = false;
                QualitySettings.softVegetation = false;
                ani = QualitySettings.anisotropicFiltering;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                shaR = QualitySettings.shadowResolution;
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.High;
                UniversalRenderPipeline.RenderSingleCamera(SRC, _reflectionCamera);
                QualitySettings.softParticles = softParticle;
                QualitySettings.softVegetation = softVeg;
                QualitySettings.billboardsFaceCameraPosition = billboard;
                QualitySettings.anisotropicFiltering = ani;
                QualitySettings.shadowResolution = shaR;
                break;
            case RenderQuality.Medium:
                softParticle = QualitySettings.softParticles;
                softVeg = QualitySettings.softVegetation;
                QualitySettings.softParticles = false;
                QualitySettings.softVegetation = false;
                billboard = QualitySettings.billboardsFaceCameraPosition;
                QualitySettings.billboardsFaceCameraPosition = false;
                shadowQuality = QualitySettings.shadows;
                QualitySettings.shadows = UnityEngine.ShadowQuality.HardOnly;
                ani = QualitySettings.anisotropicFiltering;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                shaR = QualitySettings.shadowResolution;
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.Low;
                UniversalRenderPipeline.RenderSingleCamera(SRC, _reflectionCamera);
                QualitySettings.softParticles = softParticle;
                QualitySettings.softVegetation = softVeg;
                QualitySettings.shadows = shadowQuality;
                QualitySettings.billboardsFaceCameraPosition = billboard;
                QualitySettings.anisotropicFiltering = ani;
                QualitySettings.shadowResolution = shaR;
                break;
            case RenderQuality.Low:
                softParticle = QualitySettings.softParticles;
                softVeg = QualitySettings.softVegetation;
                QualitySettings.softParticles = false;
                QualitySettings.softVegetation = false;

                shadowQuality = QualitySettings.shadows;
                QualitySettings.shadows = UnityEngine.ShadowQuality.Disable;

                ani = QualitySettings.anisotropicFiltering;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                UniversalRenderPipeline.RenderSingleCamera(SRC, _reflectionCamera);
                QualitySettings.softParticles = softParticle;
                QualitySettings.softVegetation = softVeg;

                QualitySettings.shadows = shadowQuality;
                QualitySettings.anisotropicFiltering = ani;
                break;
            case RenderQuality.VeryLow:
                ani = QualitySettings.anisotropicFiltering;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                UniversalRenderPipeline.RenderSingleCamera(SRC, _reflectionCamera);
                QualitySettings.anisotropicFiltering = ani;
                break;
        }
        GL.invertCulling = false;
    }

    public void SetTexture(RenderTexture target)
    {
        for (int i = 0, length = allMats.Count; i < length; ++i)
        {
            Material m = allMats[i];
            m.SetTexture(uniqueTextureID, target);
        }
    }

    Matrix4x4 reflection = Matrix4x4.identity;
    Matrix4x4 ref_WorldToCam;

    private Vector4 CameraSpacePlane(ref Matrix4x4 worldToCameraMatrix, Vector3 pos, Vector3 normal)
    {
        Vector3 offsetPos = pos + normal * m_ClipPlaneOffset;
        Vector3 cpos = worldToCameraMatrix.MultiplyPoint3x4(offsetPos);
        Vector3 cnormal = worldToCameraMatrix.MultiplyVector(normal).normalized;
        return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
    }

    private static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, ref Vector4 plane)
    {
        reflectionMat.m00 = (1F - 2F * plane[0] * plane[0]);
        reflectionMat.m01 = (-2F * plane[0] * plane[1]);
        reflectionMat.m02 = (-2F * plane[0] * plane[2]);
        reflectionMat.m03 = (-2F * plane[3] * plane[0]);

        reflectionMat.m10 = (-2F * plane[1] * plane[0]);
        reflectionMat.m11 = (1F - 2F * plane[1] * plane[1]);
        reflectionMat.m12 = (-2F * plane[1] * plane[2]);
        reflectionMat.m13 = (-2F * plane[3] * plane[1]);

        reflectionMat.m20 = (-2F * plane[2] * plane[0]);
        reflectionMat.m21 = (-2F * plane[2] * plane[1]);
        reflectionMat.m22 = (1F - 2F * plane[2] * plane[2]);
        reflectionMat.m23 = (-2F * plane[3] * plane[2]);
    }
}
