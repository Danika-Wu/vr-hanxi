using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class BedroomV2MaterialSetup
{
    private const string Root = "Assets/Model/Model1/bedroom_optimized_v2";
    private const string ModelPath = Root + "/mybedroom_quest_border_safe.obj";
    private const string MtlPath = Root + "/mybedroom.mtl";
    private const string TextureRoot = "Assets/Model/Model1/bedroom";
    private const string MaterialRoot = Root + "/Materials";
    private const string MarkerRelativePath = "Library/BedroomV2MaterialsConfigured.marker";

    private sealed class TexturePair
    {
        public string BaseColor;
        public string Normal;
    }

    static BedroomV2MaterialSetup()
    {
        EditorApplication.delayCall += TryAutomaticSetup;
    }

    [MenuItem("Tools/Bedroom/Rebuild V2 URP Materials")]
    public static void RebuildFromMenu()
    {
        ConfigureMaterials(force: true);
    }

    private static void TryAutomaticSetup()
    {
        string marker = Path.GetFullPath(MarkerRelativePath);
        if (File.Exists(marker) || !File.Exists(Path.GetFullPath(ModelPath)))
            return;

        ConfigureMaterials(force: false);
    }

    private static void ConfigureMaterials(bool force)
    {
        try
        {
            Dictionary<string, TexturePair> definitions = ParseMtl();
            EnsureAssetFolder(MaterialRoot);

            Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (litShader == null || unlitShader == null)
                throw new InvalidOperationException("Required URP shaders were not found.");

            var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TexturePair> item in definitions)
            {
                bool isRoomShell = string.Equals(item.Key, "tripo_material_0e1ae994-4d4e-4580-9d3f-a5e1a5d1282e", StringComparison.Ordinal);
                Shader targetShader = isRoomShell ? unlitShader : litShader;
                string safeName = item.Key.Replace('/', '_').Replace('\\', '_');
                string materialPath = MaterialRoot + "/" + safeName + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    material = new Material(targetShader) { name = item.Key };
                    AssetDatabase.CreateAsset(material, materialPath);
                }
                else
                {
                    material.shader = targetShader;
                }

                Texture2D baseColor = LoadTexture(item.Value.BaseColor, normalMap: false);
                material.SetColor("_BaseColor", Color.white);
                material.SetTexture("_BaseMap", baseColor);
                material.SetTexture("_MainTex", baseColor);
                material.SetTexture("_BumpMap", null);
                material.SetFloat("_BumpScale", 0f);
                material.SetFloat("_Smoothness", 0.14f);
                material.DisableKeyword("_NORMALMAP");
                EditorUtility.SetDirty(material);
                materials[item.Key] = material;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null)
                throw new InvalidOperationException("Could not obtain ModelImporter for V2 bedroom.");

            importer.importNormals = ModelImporterNormals.Calculate;
            importer.normalCalculationMode = ModelImporterNormalCalculationMode.AreaAndAngleWeighted;
            importer.normalSmoothingAngle = 30f;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            importer.materialSearch = ModelImporterMaterialSearch.Local;
            foreach (KeyValuePair<string, Material> item in materials)
            {
                var source = new AssetImporter.SourceAssetIdentifier(typeof(Material), item.Key);
                importer.AddRemap(source, item.Value);
            }
            importer.SaveAndReimport();

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (prefab == null)
                throw new InvalidOperationException("V2 bedroom model did not import as a GameObject.");

            int slots = 0;
            int assigned = 0;
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    slots++;
                    if (material != null && material.shader != null &&
                        material.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                        assigned++;
                }
            }
            if (assigned == 0 || assigned != slots)
                throw new InvalidOperationException($"Material verification failed: {assigned}/{slots} URP slots assigned.");

            File.WriteAllText(Path.GetFullPath(MarkerRelativePath),
                $"Configured {materials.Count} materials; verified {assigned}/{slots} renderer slots at {DateTime.Now:O}");
            Debug.Log($"[BedroomV2] Created/remapped {materials.Count} URP materials; verified {assigned}/{slots} renderer slots.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[BedroomV2] Material setup failed: " + exception);
            if (force)
                throw;
        }
    }

    private static Dictionary<string, TexturePair> ParseMtl()
    {
        string path = Path.GetFullPath(MtlPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("V2 MTL file is missing.", path);

        var result = new Dictionary<string, TexturePair>(StringComparer.Ordinal);
        string current = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("newmtl ", StringComparison.Ordinal))
            {
                current = line.Substring(7).Trim();
                result[current] = new TexturePair();
            }
            else if (current != null && line.StartsWith("map_Kd ", StringComparison.Ordinal))
            {
                result[current].BaseColor = FileNameFromMapLine(line);
            }
            else if (current != null && line.StartsWith("map_Bump ", StringComparison.Ordinal))
            {
                result[current].Normal = FileNameFromMapLine(line);
            }
        }
        if (result.Count == 0)
            throw new InvalidOperationException("No material definitions were found in the V2 MTL file.");
        return result;
    }

    private static string FileNameFromMapLine(string line)
    {
        string[] fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return Path.GetFileName(fields[fields.Length - 1].Replace('\\', '/'));
    }

    private static Texture2D LoadTexture(string fileName, bool normalMap)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        string path = TextureRoot + "/" + fileName;
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            throw new FileNotFoundException("Texture was not imported by Unity.", path);
        if (normalMap && (importer.textureType != TextureImporterType.NormalMap || importer.sRGBTexture))
        {
            importer.textureType = TextureImporterType.NormalMap;
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static void EnsureAssetFolder(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int index = 1; index < parts.Length; index++)
        {
            string next = current + "/" + parts[index];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[index]);
            current = next;
        }
    }
}
