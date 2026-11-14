#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Hands.OpenXR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
using Meta.XR;

/// <summary>
/// Applies the reusable baseline configuration for a Meta Quest project.
/// The first import runs automatically; it can also be rerun from Tools > Meta XR.
/// Scene rigs are intentionally not changed because XR Origin and OVRCameraRig must not coexist.
/// </summary>
[InitializeOnLoad]
public static class MetaXRProjectConfigurator
{
    private const string CompletionFile = "ProjectSettings/MetaXRSetup.complete";
    private const string ReportFile = "ProjectSettings/MetaXRSetupReport.json";
    private const string MetaFeatureSetId = "com.meta.openxr.featureset.metaxr";
    private static bool s_Running;

    static MetaXRProjectConfigurator()
    {
        if (!File.Exists(CompletionFile))
            EditorApplication.delayCall += RunAutomatically;
    }

    [MenuItem("Tools/Meta XR/Configure Quest Project")]
    private static void RunFromMenu()
    {
        Configure();
    }

    [MenuItem("Tools/Meta XR/Open Setup Report")]
    private static void OpenReport()
    {
        if (File.Exists(ReportFile))
            EditorUtility.RevealInFinder(Path.GetFullPath(ReportFile));
        else
            Debug.LogWarning("Meta XR setup report has not been generated yet.");
    }

    private static void RunAutomatically()
    {
        Configure();
    }

    private static void Configure()
    {
        if (s_Running || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            if (!s_Running)
                EditorApplication.delayCall += RunAutomatically;
            return;
        }

        s_Running = true;
        var messages = new List<string>();
        try
        {
            ConfigurePlayerSettings(messages);
            ConfigureTarget(BuildTargetGroup.Android, true, messages);
            ConfigureTarget(BuildTargetGroup.Standalone, false, messages);

            AssetDatabase.SaveAssets();
            WriteReport(messages, null);
            File.WriteAllText(CompletionFile, DateTime.UtcNow.ToString("O"));
            Debug.Log("[Meta XR Setup] Quest/OpenXR configuration completed. Report: " + ReportFile);
        }
        catch (Exception exception)
        {
            WriteReport(messages, exception.ToString());
            Debug.LogException(exception);
        }
        finally
        {
            s_Running = false;
        }
    }

    private static void ConfigurePlayerSettings(List<string> messages)
    {
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.gpuSkinning = true;
        messages.Add("Android: IL2CPP, ARM64, API 32 minimum/API 34 target, Linear color space, GPU skinning enabled.");
    }

    private static void ConfigureTarget(BuildTargetGroup group, bool enableMetaQuestSupport, List<string> messages)
    {
        var generalSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        if (generalSettings == null || generalSettings.Manager == null)
            throw new InvalidOperationException($"XR Plug-in Management settings are missing for {group}.");

        if (!generalSettings.Manager.activeLoaders.Any(loader => loader is OpenXRLoader))
        {
            if (!XRPackageMetadataStore.AssignLoader(generalSettings.Manager, typeof(OpenXRLoader).FullName, group))
                throw new InvalidOperationException($"Could not assign the OpenXR loader for {group}.");
        }

        generalSettings.Manager.automaticLoading = true;
        generalSettings.Manager.automaticRunning = true;
        EditorUtility.SetDirty(generalSettings.Manager);

        var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(group, MetaFeatureSetId);
        if (featureSet != null && !featureSet.isEnabled)
        {
            featureSet.isEnabled = true;
            OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(group);
        }

        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        if (settings == null)
            throw new InvalidOperationException($"OpenXR settings are missing for {group}.");

        if (enableMetaQuestSupport)
            EnableAll<MetaQuestFeature>(settings);

        EnableAll<OculusTouchControllerProfile>(settings);
        EnableAll<MetaQuestTouchPlusControllerProfile>(settings);
        EnableAll<MetaQuestTouchProControllerProfile>(settings);
        EnableAll<OculusTouchControllerProximityProfile>(settings);
        EnableAll<HandTracking>(settings);
        EnableAll<MetaHandTrackingAim>(settings);

        EditorUtility.SetDirty(settings);
        EditorUtility.SetDirty(generalSettings);
        messages.Add($"{group}: OpenXR loader and Meta controller/hand-tracking baseline enabled.");
    }

    private static void EnableAll<T>(OpenXRSettings settings) where T : OpenXRFeature
    {
        foreach (var feature in settings.GetFeatures<T>().OfType<T>())
        {
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
        }
    }

    private static void WriteReport(List<string> messages, string error)
    {
        var report = new SetupReport
        {
            generatedUtc = DateTime.UtcNow.ToString("O"),
            success = string.IsNullOrEmpty(error),
            activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
            scriptingBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android).ToString(),
            androidArchitectures = PlayerSettings.Android.targetArchitectures.ToString(),
            colorSpace = PlayerSettings.colorSpace.ToString(),
            android = TargetReport(BuildTargetGroup.Android),
            standalone = TargetReport(BuildTargetGroup.Standalone),
            messages = messages.ToArray(),
            error = error
        };

        File.WriteAllText(ReportFile, JsonUtility.ToJson(report, true));
    }

    private static TargetSetupReport TargetReport(BuildTargetGroup group)
    {
        var generalSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        return new TargetSetupReport
        {
            target = group.ToString(),
            openXRLoaderEnabled = generalSettings != null && generalSettings.Manager != null &&
                                  generalSettings.Manager.activeLoaders.Any(loader => loader is OpenXRLoader),
            enabledFeatures = settings == null
                ? Array.Empty<string>()
                : settings.GetFeatures().Where(feature => feature != null && feature.enabled)
                    .Select(feature => feature.name).Distinct().OrderBy(name => name).ToArray()
        };
    }

    [Serializable]
    private class SetupReport
    {
        public string generatedUtc;
        public bool success;
        public string activeBuildTarget;
        public string scriptingBackend;
        public string androidArchitectures;
        public string colorSpace;
        public TargetSetupReport android;
        public TargetSetupReport standalone;
        public string[] messages;
        public string error;
    }

    [Serializable]
    private class TargetSetupReport
    {
        public string target;
        public bool openXRLoaderEnabled;
        public string[] enabledFeatures;
    }
}
#endif
