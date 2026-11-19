// The game's per-frame constants as its shaders see them (cbuffer Frame,
// Content\Shaders\Frame.hlsli and Common.hlsli of Space Engineers 1.210):
// the bytes of MyCommon.FrameConstantsData, which the game uploads as they
// are, arrive every frame through RtFrame (rt_core.cpp binds them at b1).
// Only the layout is mirrored here, field for field; 1280 bytes.

#ifndef GAME_FRAME_HLSLI
#define GAME_FRAME_HLSLI

struct GameEnvironmentSettings
{
    float4x4 view_matrix;
    float4x4 projection_matrix;
    float4x4 projection_matrix_for_skybox;
    float4x4 view_projection_matrix;
    float4x4 inv_view_matrix;
    float4x4 inv_proj_matrix;
    float4x4 inv_view_proj_matrix;
    float4x4 background_orientation;  // skybox
    float4 world_offset;
    float3 eye_offset_in_world;
    float __pad0;
    float3 cameraPositionDelta;
    float __pad1;
};

struct GameScreenSettings
{
    uint2 offset;
    float2 resolution;
    float drs;
    float __pad0, __pad1, __pad2;
};

struct GameFoliageSettings
{
    float4 clipping_scaling;
    float3 wind_vec;
    float __pad;
};

struct GameLightSettings
{
    float3 directionalLightColor;
    float directionalLightDiffuseFactor;

    float3 directionalLightVec;  // the direction the sun's light travels
    float aoDirLight;

    float directionalLightGlossFactor;
    float ambientDiffuseFactor;
    float ambientSpecularFactor;
    float forwardPassAmbient;

    float3 SunDiscColor;
    float SunDiscInnerDot;

    float3 SunDiscColor2;
    float _pad1;

    float SunDiscOuterDot;
    float aoIndirectLight, aoPointLight, aoSpotLight;

    float skyboxBrightness;
    float envSkyboxBrightness;
    float shadowFadeout;
    float envShadowFadeout;

    float envAtmosphereBrightness;
    float AmbientFarDistance;
    float TransparentAmbient;
    float ForwardDimDistance;

    float SunDiscIntensity;
    float SkipIBLevels;
    float directionalLightSpecularFactor;
    uint tiles_num;

    float3 directionalLightSpecularColor;
    uint tiles_x;
    uint maxTileLights;
    float3 __pad0;
};

struct GamePostProcessSettings
{
    float Contrast;
    float Brightness;
    float ConstantLuminance;
    float LuminanceExposure;

    float Saturation;
    float BrightnessFactorR;
    float BrightnessFactorG;
    float BrightnessFactorB;

    float3 TemperatureColor;
    float TemperatureStrength;

    float Vibrance;
    float EyeAdaptationTau;
    float BloomExposure;
    float BloomLumaThreshold;

    float BloomMult;
    float BloomEmissiveness;
    float BloomDepthStrength;
    float BloomDepthSlope;

    float3 LightColor;
    float LogLumThreshold;

    float3 DarkColor;
    float SepiaStrength;

    float EyeAdaptationSpeedUp;
    float EyeAdaptationSpeedDown;
    float WhitePoint;
    float BloomDirtRatio;

    int GrainSize;
    float GrainAmount;
    float GrainStrength;
    float ChromaticFactor;

    float VignetteStart;
    float VignetteLength;
    float Res0, Res1;
};

struct GameFogSettings
{
    float3 color;
    float density;
    float mult;
    float sky;
    float atmo;
    float __pad1;
};

struct GameVoxelSettings
{
    float4 LodRanges[12];
    float DebugVoxelLod;
    float __pad1, __pad2, __pad3;
};

struct GameTextureDebugMultipliers
{
    float AlbedoMultiplier;
    float MetalnessMultiplier;
    float GlossMultiplier;
    float AoMultiplier;

    float EmissiveMultiplier;
    float ColorMaskMultiplier;
    float AlbedoShift;
    float MetalnessShift;

    float GlossShift;
    float AoShift;
    float EmissiveShift;
    float ColorMaskShift;

    float4 ColorizeHSV;
};

struct GameFrameConstants
{
    GameEnvironmentSettings Environment;
    GameScreenSettings Screen;
    GameFoliageSettings Foliage;
    GameLightSettings Light;
    GamePostProcessSettings Post;
    GameFogSettings Fog;
    GameVoxelSettings Voxels;
    GameTextureDebugMultipliers TextureDebugMultipliers;
    float frameTime;
    float frameTimeDelta;
    float randomSeed;
    float DistanceFade;
};

#endif
