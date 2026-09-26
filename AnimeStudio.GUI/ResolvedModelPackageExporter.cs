using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityObject = AnimeStudio.Object;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Builds a model-scoped, resolved archive package from the exact Unity objects
    /// already loaded by AnimeStudio. Dependency loading remains AssetsManager/CABMap's job.
    /// This exporter never invents geometry/material slots and fails closed on unresolved
    /// model-scoped pointers.
    /// </summary>
    internal static class ResolvedModelPackageExporter
    {
        private const int MaxObjects = 5000;
        private const int MaxEdges = 25000;

        private sealed class Manifest
        {
            public int FormatVersion = 2;
            public string Model;
            public string ValidationStatus;
            public bool ResolveDependenciesEnabled;
            public bool CABMapLoaded;
            public int CABMapEntries;
            public string CABMapBaseFolder;
            public bool DummyDllLoaded;
            public int DummyDllAssemblyCount;
            public List<ModelRootIdentity> Roots = new();
            public List<RendererMaterialDependency> RendererMaterialDependencies = new();
            public List<Edge> DependencyEdges = new();
            public List<string> Errors = new();
            public List<string> Warnings = new();
            public Dictionary<string, int> ObjectCounts = new(StringComparer.Ordinal);
        }


        private sealed class Edge
        {
            public ObjectIdentity From;
            public string Property;
            public int FileID;
            public long PathID;
            public string TargetCAB;
            public bool Resolved;
            public ObjectIdentity To;
            public bool Heuristic;
        }

        private sealed class ObjectIdentity
        {
            public string Type;
            public string Name;
            public string SourceCAB;
            public string OriginalPath;
            public long PathID;
        }

        private sealed class PointerInfo
        {
            public string Property;
            public int FileID;
            public long PathID;
            public string TargetCAB;
            public bool Heuristic;
        }

        private sealed class MaterialFile
        {
            public ObjectIdentity Identity;
            public object Shader;
            public object ShaderKeywords;
            public string[] ValidKeywords;
            public string[] InvalidKeywords;
            public uint LightmapFlags;
            public bool EnableInstancingVariants;
            public bool DoubleSidedGI;
            public int CustomRenderQueue;
            public List<KeyValuePair<string, string>> StringTagMap;
            public string[] DisabledShaderPasses;
            public List<object> TextureBindings = new();
            public List<KeyValuePair<string, int>> Ints;
            public List<KeyValuePair<string, float>> Floats;
            public List<KeyValuePair<string, Color>> Colors;
        }

        private sealed class TextureFile
        {
            public ObjectIdentity Identity;
            public int Width;
            public int Height;
            public int CompleteImageSize;
            public int MipsStripped;
            public string TextureFormat;
            public bool MipMap;
            public int MipCount;
            public bool IsReadable;
            public bool IsPreProcessed;
            public bool IgnoreMasterTextureLimit;
            public bool StreamingMipmaps;
            public int StreamingMipmapsPriority;
            public int ImageCount;
            public int TextureDimension;
            public int LightmapFormat;
            public int ColorSpace;
            public byte[] PlatformBlob;
            public uint ExternalMipRelativeOffset;
            public object Sampler;
            public object Stream;
            public string ExactPayload;
            public string ConvertedImage;
            public string PayloadSHA256;
        }

        private sealed class CubemapFile
        {
            public ObjectIdentity Identity;
            public int Width;
            public int Height;
            public int CompleteImageSize;
            public string TextureFormat;
            public bool MipMap;
            public int MipCount;
            public bool IsReadable;
            public bool IsPreProcessed;
            public bool IgnoreMasterTextureLimit;
            public bool StreamingMipmaps;
            public int StreamingMipmapsPriority;
            public int ImageCount;
            public int TextureDimension;
            public int LightmapFormat;
            public int ColorSpace;
            public object[] SourceTextures;
            public object Sampler;
            public object Stream;
            public string ExactPayload;
            public string PayloadSHA256;
        }

        private sealed class ShaderPassProgramRef
        {
            public int SubShaderIndex;
            public int PassIndex;
            public string PassName;
            public string UseName;
            public string Stage;
            public int StageVariantIndex;
            public uint BlobIndex;
            public string GpuProgramType;
            public sbyte HardwareTier;
            public ParserBindChannels BindChannels;
            public ushort[] KeywordIndices;
            public ushort[] GlobalKeywordIndices;
            public ushort[] LocalKeywordIndices;
        }

        private sealed class Context
        {
            public string Root;
            public string FbxPath;
            public Manifest Manifest;
            public readonly Queue<UnityObject> Queue = new();
            public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> HierarchyNames = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ComponentIds = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ComponentNodes = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> FileNameCounts = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Shader> Shaders = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string[]>> ShaderKeywordSets = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> MaterialPaths = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> TexturePaths = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> CubemapPaths = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<object> TextureAssets = new();
            public readonly List<object> CubemapAssets = new();
            public readonly List<object> RuntimeEntries = new();
            public int EdgeCount;
        }

        public static void Export(ModelConverter converter, string fbxPath)
        {
            if (converter == null || converter.RootGameObjects.Count == 0) return;

            var modelName = Path.GetFileNameWithoutExtension(fbxPath);
            var finalRoot = Path.GetDirectoryName(fbxPath) ?? string.Empty;
            var tempRoot = Path.Combine(finalRoot, ".animestudio-resolved-tmp-" + Fix(modelName));
            var obsoleteRoot = Path.Combine(finalRoot, modelName + ".resolved");
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            Directory.CreateDirectory(tempRoot);

            var manifest = new Manifest
            {
                Model = modelName,
                ResolveDependenciesEnabled = Studio.assetsManager.ResolveDependencies,
                CABMapLoaded = AssetsHelper.CABMapLoaded,
                CABMapEntries = AssetsHelper.CABMapCount,
                CABMapBaseFolder = AssetsHelper.CABMapBaseFolder,
                DummyDllLoaded = Studio.assemblyLoader.Loaded,
                DummyDllAssemblyCount = Studio.assemblyLoader.LoadedAssemblyCount,
                Roots = converter.RootIdentities.ToList(),
                RendererMaterialDependencies = converter.RendererMaterialDependencies.ToList()
            };
            var ctx = new Context { Root = tempRoot, FbxPath = fbxPath, Manifest = manifest };

            try
            {
                if (!manifest.ResolveDependenciesEnabled)
                    manifest.Errors.Add("AnimeStudio 'Resolve dependencies' is disabled. Reload the source corpus with dependency resolution enabled.");
                if (!manifest.CABMapLoaded)
                    manifest.Errors.Add("No CAB map is loaded. Load the Genshin CAB map before resolved export.");

                foreach (var root in converter.RootGameObjects)
                    CollectHierarchy(ctx, root, Fix(root.Name));

                ProcessQueue(ctx);
                ValidateRendererDependencies(ctx);
                ExportCollectedShaders(ctx);
                WriteHierarchy(ctx, converter.RootGameObjects);
                WriteRuntimeManifest(ctx);
                CleanupRedundantDefaultMaterial(ctx);

                manifest.DummyDllLoaded = Studio.assemblyLoader.Loaded;
                manifest.DummyDllAssemblyCount = Studio.assemblyLoader.LoadedAssemblyCount;
                manifest.ValidationStatus = manifest.Errors.Count == 0 ? "VALIDATION_OK" : "VALIDATION_FAILED";

                var unresolved = manifest.DependencyEdges.Where(x => !x.Resolved).ToList();
                WriteJson(Path.Combine(tempRoot, "dependencies.json"), new
                {
                    shader_path = "Shaders",
                    texture_assets = ctx.TextureAssets,
                    cubemap_assets = ctx.CubemapAssets,
                    runtime_path = "Runtime",
                    runtime_manifest = "Runtime/runtime.json",
                    external_assets = Array.Empty<object>(),
                    validation_status = manifest.ValidationStatus,
                    resolution = new
                    {
                        edges = manifest.DependencyEdges.Count,
                        unresolved = unresolved.Count,
                        objects = manifest.ObjectCounts,
                        cab_map_entries = manifest.CABMapEntries,
                        dummy_dll_loaded = manifest.DummyDllLoaded,
                        dummy_dll_assemblies = manifest.DummyDllAssemblyCount
                    },
                    unresolved_dependencies = unresolved,
                    renderer_material_dependencies = manifest.RendererMaterialDependencies,
                    warnings = manifest.Warnings
                });

                if (manifest.Errors.Count > 0)
                    File.WriteAllLines(Path.Combine(tempRoot, "VALIDATION_FAILED.txt"), manifest.Errors);
            }
            catch (Exception ex)
            {
                manifest.Errors.Add("Exporter exception: " + ex);
                File.WriteAllLines(Path.Combine(tempRoot, "VALIDATION_FAILED.txt"), manifest.Errors);
            }

            // Remove the obsolete v2 synthetic package if present, then merge the
            // clean archive-shaped output beside the FBX. Normal AnimeStudio files
            // (FBX and model-metadata.json) stay exactly where they already are.
            if (Directory.Exists(obsoleteRoot)) Directory.Delete(obsoleteRoot, true);
            foreach (var generatedDir in new[] { "Shaders", "Runtime", "Cubemaps" })
            {
                var path = Path.Combine(finalRoot, generatedDir);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            MergeDirectory(tempRoot, finalRoot);
            Directory.Delete(tempRoot, true);

            if (manifest.Errors.Count > 0)
                throw new InvalidOperationException($"Resolved export validation failed for {modelName}. See {Path.Combine(finalRoot, "VALIDATION_FAILED.txt")}");

            var staleFailure = Path.Combine(finalRoot, "VALIDATION_FAILED.txt");
            if (File.Exists(staleFailure)) File.Delete(staleFailure);
            Logger.Info($"Resolved model export completed in-place: {finalRoot}");
        }

        private static void CollectHierarchy(Context ctx, GameObject go, string path)
        {
            if (go == null) return;
            var goKey = Key(go);
            if (ctx.HierarchyNames.ContainsKey(goKey)) return;
            ctx.HierarchyNames[goKey] = path;
            Enqueue(ctx, go);

            if (go.m_Components != null)
            {
                var ordinal = 0;
                foreach (var pptr in go.m_Components)
                {
                    ordinal++;
                    if (pptr == null || pptr.IsNull) continue;
                    if (pptr.TryGet(out Component component))
                    {
                        var componentKey = Key(component);
                        var componentId = $"component:{path}/{component.GetType().Name}#{ordinal}";
                        ctx.HierarchyNames[componentKey] = path;
                        ctx.ComponentIds[componentKey] = componentId;
                        ctx.ComponentNodes[componentKey] = "node:" + path;
                        Enqueue(ctx, component);
                    }
                    else if (pptr.TryGet<UnityObject>(out var rawComponent))
                    {
                        ctx.HierarchyNames[Key(rawComponent)] = path + "__" + rawComponent.type + "__" + ordinal;
                        Enqueue(ctx, rawComponent);
                        var rawType = rawComponent.type.ToString();
                        if (rawType.IndexOf("Renderer", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // A raw renderer can hide authoritative material slots/effect dependencies,
                            // so do not claim a complete package when its Renderer base could not be parsed.
                            ctx.Manifest.Errors.Add($"Renderer component {Describe(rawComponent)} is not represented by a parsed AnimeStudio Renderer class; exact raw bytes were kept, but material-slot closure cannot be proven.");
                        }
                        else
                        {
                            // Native non-renderer components can safely remain provenance-only for this
                            // rendering-focused package. Their exact bytes are still archived.
                            ctx.Manifest.Warnings.Add($"Native component {Describe(rawComponent)} has no parsed AnimeStudio Component class; exact raw bytes are kept as provenance and it is not traversed structurally.");
                        }
                    }
                    else
                    {
                        AddUnresolved(ctx, go, "m_Components[" + (ordinal - 1) + "]", pptr.m_FileID, pptr.m_PathID, pptr.GetTargetFileName());
                    }
                }
            }

            if (go.m_Transform?.m_Children == null) return;
            foreach (var childPtr in go.m_Transform.m_Children)
            {
                if (childPtr.TryGet(out var childTransform))
                {
                    if (childTransform.m_GameObject.TryGet(out var childGo))
                        CollectHierarchy(ctx, childGo, path + "/" + Fix(childGo.Name));
                    else if (!childTransform.m_GameObject.IsNull)
                        AddUnresolved(ctx, childTransform, "m_GameObject", childTransform.m_GameObject.m_FileID, childTransform.m_GameObject.m_PathID, childTransform.m_GameObject.GetTargetFileName());
                }
                else if (!childPtr.IsNull)
                {
                    AddUnresolved(ctx, go, "Transform.m_Children", childPtr.m_FileID, childPtr.m_PathID, childPtr.GetTargetFileName());
                }
            }
        }

        private static void ProcessQueue(Context ctx)
        {
            while (ctx.Queue.Count > 0)
            {
                if (ctx.Seen.Count > MaxObjects)
                {
                    ctx.Manifest.Errors.Add($"Model dependency closure exceeded safety limit ({MaxObjects} objects). Export stopped instead of traversing unrelated libraries.");
                    return;
                }

                var obj = ctx.Queue.Dequeue();
                ExportObject(ctx, obj);
                Count(ctx, obj.type.ToString());

                if (!ShouldTraversePointers(obj)) continue;
                foreach (var ptr in GetPointers(obj))
                {
                    if (!ResolveEdge(ctx, obj, ptr)) return;
                }
            }
        }

        private static bool ShouldTraversePointers(UnityObject obj)
        {
            if (obj is Transform || obj is Animator || obj is Animation || obj is AnimationClip ||
                obj is Mesh || obj is Texture2D || obj is MonoScript || obj is TextAsset ||
                obj is AudioClip || obj is VideoClip)
                return false;
            return obj.type.ToString().IndexOf("Controller", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool ResolveEdge(Context ctx, UnityObject source, PointerInfo ptr)
        {
            if (ptr.PathID == 0 || ptr.FileID < 0) return true;
            if (ctx.EdgeCount++ >= MaxEdges)
            {
                ctx.Manifest.Errors.Add($"Model dependency closure exceeded safety limit ({MaxEdges} PPtr edges).");
                return false;
            }
            var edge = new Edge
            {
                From = Identity(source), Property = ptr.Property, FileID = ptr.FileID,
                PathID = ptr.PathID, TargetCAB = ptr.TargetCAB, Heuristic = ptr.Heuristic
            };
            var pptr = new PPtr<UnityObject>(ptr.FileID, ptr.PathID, source.assetsFile);
            if (pptr.TryGet(out var target))
            {
                edge.Resolved = true;
                edge.To = Identity(target);
                Enqueue(ctx, target);
                if (ptr.Heuristic)
                    ctx.Manifest.Warnings.Add($"Recovered contextual dependency from opaque serialized tail: {Describe(source)} :: {ptr.Property} -> {Describe(target)}.");
                if (target is GameObject targetGo)
                    CollectHierarchy(ctx, targetGo, "Dependency/" + Fix(targetGo.Name));
            }
            else
            {
                edge.Resolved = false;
                ctx.Manifest.Errors.Add($"Unresolved PPtr: {Describe(source)} :: {ptr.Property} -> FileID={ptr.FileID}, PathID={ptr.PathID}, targetCAB={ptr.TargetCAB ?? "?"}");
            }
            ctx.Manifest.DependencyEdges.Add(edge);
            return true;
        }

        private static void AddUnresolved(Context ctx, UnityObject source, string property, int fileId, long pathId, string cab)
        {
            ctx.Manifest.DependencyEdges.Add(new Edge { From = Identity(source), Property = property, FileID = fileId, PathID = pathId, TargetCAB = cab, Resolved = false });
            ctx.Manifest.Errors.Add($"Unresolved PPtr: {Describe(source)} :: {property} -> FileID={fileId}, PathID={pathId}, targetCAB={cab ?? "?"}");
        }

        private static void Enqueue(Context ctx, UnityObject obj)
        {
            if (obj == null) return;
            var key = Key(obj);
            if (ctx.Seen.Add(key)) ctx.Queue.Enqueue(obj);
        }

        private static void ExportObject(Context ctx, UnityObject obj)
        {
            switch (obj)
            {
                case Material material: ExportMaterial(ctx, material); break;
                case Texture2D texture: ExportTexture(ctx, texture); break;
                case Cubemap cubemap: ExportCubemap(ctx, cubemap); break;
                case Shader shader: CollectShader(ctx, shader); break;
                case Component component: ExportComponent(ctx, component); break;
                // Mesh geometry is already represented by the FBX. GameObject hierarchy
                // is aggregated into Runtime/hierarchy.json instead of hundreds of files.
                case Mesh: break;
                case GameObject: break;
                default: break;
            }
        }

        private static void ExportMaterial(Context ctx, Material mat)
        {
            var relativePath = MaterialRelativePath(ctx, mat);
            Directory.CreateDirectory(Dir(ctx, "Materials"));

            // Keep the mature post-resolver material schema so the viewer/archive
            // does not need a second translation layer. Add native 2021 keyword
            // arrays + contextual identity as additive fields.
            var keywordString = string.Join(" ", GetMaterialKeywords(mat));
            WriteJson(Path.Combine(ctx.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)), new
            {
                m_Shader = mat.m_Shader,
                m_SavedProperties = mat.m_SavedProperties,
                m_Name = mat.m_Name,
                Name = mat.Name,
                m_ShaderKeywords = keywordString,
                m_ValidKeywords = mat.m_ValidKeywords,
                m_InvalidKeywords = mat.m_InvalidKeywords,
                m_LightmapFlags = mat.m_LightmapFlags,
                m_EnableInstancingVariants = mat.m_EnableInstancingVariants,
                m_DoubleSidedGI = mat.m_DoubleSidedGI,
                m_CustomRenderQueue = mat.m_CustomRenderQueue,
                stringTagMap = mat.m_StringTagMap,
                disabledShaderPasses = mat.m_DisabledShaderPasses,
                unity = Identity(mat)
            });

            if (mat.m_Shader != null && mat.m_Shader.TryGet(out Shader shader))
            {
                var shaderKey = Key(shader);
                ctx.Shaders[shaderKey] = shader;
                if (!ctx.ShaderKeywordSets.TryGetValue(shaderKey, out var sets))
                    ctx.ShaderKeywordSets[shaderKey] = sets = new List<string[]>();
                var keywords = GetMaterialKeywords(mat);
                if (!sets.Any(x => x.SequenceEqual(keywords))) sets.Add(keywords);
            }
        }

        private static void ExportTexture(Context ctx, Texture2D tex)
        {
            var convertedPath = TextureRelativePath(ctx, tex);
            var baseName = Path.GetFileNameWithoutExtension(convertedPath);
            string converted = null;
            string sha = null;
            try
            {
                using var image = tex.ConvertToImage(true);
                if (image == null) throw new InvalidOperationException("ConvertToImage returned null");
                converted = convertedPath;
                var output = Path.Combine(ctx.Root, converted.Replace('/', Path.DirectorySeparatorChar));
                using (var fs = File.Create(output)) image.WriteToStream(fs, ImageFormat.Png);
                sha = SHA256File(output);
            }
            catch (Exception ex)
            {
                ctx.Manifest.Errors.Add($"Texture conversion failed for {Describe(tex)}: {ex.Message}");
            }

            ctx.TextureAssets.Add(new
            {
                name = tex.Name,
                cab = tex.assetsFile?.fileName,
                path_id = tex.m_PathID,
                class_id = (int)tex.type,
                asset_kind = "Texture2D",
                payload_kind = "converted_image",
                payloads = converted == null ? Array.Empty<string>() : new[] { converted },
                payload_sha256 = sha,
                width = tex.m_Width, height = tex.m_Height, format = tex.m_TextureFormat.ToString(),
                mip_count = tex.m_MipCount, color_space = tex.m_ColorSpace,
                filter_mode = tex.m_TextureSettings?.m_FilterMode, aniso = tex.m_TextureSettings?.m_Aniso,
                mip_bias = tex.m_TextureSettings?.m_MipBias, wrap_u = tex.m_TextureSettings?.m_WrapU,
                wrap_v = tex.m_TextureSettings?.m_WrapV, wrap_w = tex.m_TextureSettings?.m_WrapW,
                stream = tex.m_StreamData
            });
        }

        private static void ExportCubemap(Context ctx, Cubemap cube)
        {
            var cubemapRelative = CubemapRelativePath(ctx, cube);
            var baseName = Path.GetFileNameWithoutExtension(cubemapRelative);
            var dir = Path.Combine(ctx.Root, Path.GetDirectoryName(cubemapRelative.Replace('/', Path.DirectorySeparatorChar)) ?? "Cubemaps");
            Directory.CreateDirectory(dir);
            var payloadName = baseName + ".payload.bin";
            string sha = null;
            try
            {
                var data = cube.image_data.GetData();
                File.WriteAllBytes(Path.Combine(dir, payloadName), data);
                sha = SHA256Hex(data);
            }
            catch (Exception ex)
            {
                ctx.Manifest.Errors.Add($"Cubemap payload unavailable for {Describe(cube)}: {ex.Message}");
            }
            var metadata = new
            {
                name = cube.Name, identity = Identity(cube), width = cube.m_Width, height = cube.m_Height,
                format = cube.m_TextureFormat.ToString(), mip_count = cube.m_MipCount, image_count = cube.m_ImageCount,
                dimension = cube.m_TextureDimension, color_space = cube.m_ColorSpace, sampler = cube.m_TextureSettings,
                stream = cube.m_StreamData, source_textures = cube.m_SourceTextures?.Select(x => PointerDto(x)).ToArray(),
                payload = payloadName, payload_sha256 = sha
            };
            WriteJson(Path.Combine(dir, baseName + ".json"), metadata);
            var payloadRelative = (Path.GetDirectoryName(cubemapRelative)?.Replace('\\', '/') ?? "Cubemaps") + "/" + payloadName;
            ctx.CubemapAssets.Add(new { name = cube.Name, path = cubemapRelative, payload = payloadRelative, sha256 = sha });
        }

        private static void CollectShader(Context ctx, Shader shader)
        {
            ctx.Shaders[Key(shader)] = shader;
        }

        private static void ExportCollectedShaders(Context ctx)
        {
            foreach (var pair in ctx.Shaders.OrderBy(x => x.Value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var shader = pair.Value;
                if (shader.m_IsRawOnly || shader.m_ParsedForm == null)
                {
                    ctx.Manifest.Errors.Add($"Referenced shader is still raw-only/unparsed: {Describe(shader)}. Genshin structured shader export is incomplete.");
                    continue;
                }
                try
                {
                    ExportCleanShader(ctx, shader, ctx.ShaderKeywordSets.TryGetValue(pair.Key, out var sets) ? sets : new List<string[]> { Array.Empty<string>() });
                }
                catch (Exception ex)
                {
                    ctx.Manifest.Errors.Add($"Shader export failed for {Describe(shader)}: {ex.Message}");
                }
            }
        }

        private sealed class ProgramChoice
        {
            public ShaderPassProgramRef Ref;
            public ShaderConverter.ExtractedShaderProgram Program;
            public string[] DynamicKeywords;
            public int StaticMatchCount;
        }

        private static void ExportCleanShader(Context ctx, Shader shader, List<string[]> keywordSets)
        {
            var shaderName = string.IsNullOrWhiteSpace(shader.Name) ? "Shader" : shader.Name;
            var shaderRoot = Path.Combine(Dir(ctx, "Shaders"), SafeShaderPath(shaderName));
            Directory.CreateDirectory(shaderRoot);

            var programs = shader.ExtractPrograms();
            if (shader.compressedBlob != null && programs.Count == 0)
                throw new InvalidOperationException("serialized program blob exists but no GPU programs were extracted");
            var passRefs = BuildPassProgramMap(shader);
            var programByBlob = programs
                .GroupBy(x => x.VariantIndex)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Platform == ShaderCompilerPlatform.D3D11).First());

            var variants = new List<object>();
            foreach (var activeKeywords in keywordSets.OrderBy(x => string.Join(" ", x), StringComparer.Ordinal))
            {
                var passes = new List<object>();
                for (var subIndex = 0; subIndex < shader.m_ParsedForm.m_SubShaders.Count; subIndex++)
                {
                    var sub = shader.m_ParsedForm.m_SubShaders[subIndex];
                    for (var passIndex = 0; passIndex < sub.m_Passes.Count; passIndex++)
                    {
                        var pass = sub.m_Passes[passIndex];
                        var refs = passRefs.Where(x => x.SubShaderIndex == subIndex && x.PassIndex == passIndex).ToList();
                        var defaultPass = BuildSelectedPass(ctx, shader, shaderRoot, pass, refs, programByBlob, activeKeywords, null);
                        if (defaultPass != null) passes.Add(defaultPass);

                        // Unity shadow caster variants are runtime-selected rather than material keywords.
                        // Preserve the two useful engine conditions without dumping hundreds of variants.
                        foreach (var dynamicKeyword in new[] { "SHADOWS_CUBE", "SHADOWS_DEPTH" })
                        {
                            if (refs.Any(r => ProgramKeywords(programByBlob, r).Contains(dynamicKeyword, StringComparer.Ordinal)))
                            {
                                var conditional = BuildSelectedPass(ctx, shader, shaderRoot, pass, refs, programByBlob, activeKeywords, dynamicKeyword);
                                if (conditional != null) passes.Add(conditional);
                            }
                        }
                    }
                }
                variants.Add(new { keywords = activeKeywords, passes });
            }

            var properties = shader.m_ParsedForm.m_PropInfo?.m_Props?.Select(p => new
            {
                name = p.m_Name, type = (int)p.m_Type, flags = (int)p.m_Flags, @default = p.m_DefValue,
                description = p.m_Description, attributes = p.m_Attributes, texture_dimension = (int)(p.m_DefTexture?.m_TexDim ?? TextureDimension.Unknown),
                default_texture = p.m_DefTexture?.m_DefaultName
            }).ToList();

            WriteJson(Path.Combine(shaderRoot, "shader.json"), new
            {
                name = shaderName,
                properties,
                variants,
                dependencies = shader.m_Dependencies?.Select(x => PointerDto(x)).ToArray(),
                non_modifiable_textures = shader.m_NonModifiableTextures?.Select(x => new { name = x.Key, texture = PointerDto(x.Value) }).ToArray()
            });
        }

        private static object BuildSelectedPass(Context ctx, Shader shader, string shaderRoot, SerializedPass pass, List<ShaderPassProgramRef> refs, Dictionary<int, ShaderConverter.ExtractedShaderProgram> programByBlob, string[] activeKeywords, string requiredDynamicKeyword)
        {
            var passName = !string.IsNullOrWhiteSpace(pass.m_Name) ? pass.m_Name : !string.IsNullOrWhiteSpace(pass.m_UseName) ? pass.m_UseName : "PASS";
            var conditionName = requiredDynamicKeyword ?? "default";
            var passDir = Path.Combine(shaderRoot, Fix(passName), Fix(conditionName));
            string vertex = null, fragment = null, geometry = null, hull = null, domain = null;
            object vertexBind = null, fragmentBind = null;

            foreach (var stage in new[] { "vertex", "fragment", "geometry", "hull", "domain" })
            {
                var choice = SelectProgramChoice(shader, refs.Where(x => x.Stage == stage), programByBlob, activeKeywords, requiredDynamicKeyword);
                if (choice == null) continue;
                Directory.CreateDirectory(passDir);
                var bytes = choice.Program.ProgramCode ?? Array.Empty<byte>();
                var hash = SHA256Hex(bytes).Substring(0, 12);
                var fileName = stage + "." + hash + ProgramExtension(choice.Program.ProgramType);
                File.WriteAllBytes(Path.Combine(passDir, fileName), bytes);
                var relative = Path.GetRelativePath(shaderRoot, Path.Combine(passDir, fileName)).Replace('\\', '/');
                switch (stage)
                {
                    case "vertex": vertex = relative; vertexBind = choice.Ref.BindChannels; break;
                    case "fragment": fragment = relative; fragmentBind = choice.Ref.BindChannels; break;
                    case "geometry": geometry = relative; break;
                    case "hull": hull = relative; break;
                    case "domain": domain = relative; break;
                }
            }

            if (vertex == null && fragment == null && string.IsNullOrWhiteSpace(pass.m_UseName)) return null;
            return new
            {
                name = passName, type = (int)pass.m_Type, state = ShaderStateDto(pass.m_State),
                when = requiredDynamicKeyword == null ? null : new[] { requiredDynamicKeyword },
                vertex, fragment, geometry, hull, domain, vertex_bind_channels = vertexBind, fragment_bind_channels = fragmentBind,
                use_name = string.IsNullOrWhiteSpace(pass.m_UseName) ? null : pass.m_UseName
            };
        }

        private static ProgramChoice SelectProgramChoice(Shader shader, IEnumerable<ShaderPassProgramRef> refs, Dictionary<int, ShaderConverter.ExtractedShaderProgram> programByBlob, string[] activeKeywords, string requiredDynamicKeyword)
        {
            var active = new HashSet<string>(activeKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);
            var choices = new List<ProgramChoice>();
            foreach (var r in refs)
            {
                if (!programByBlob.TryGetValue((int)r.BlobIndex, out var program)) continue;
                var staticKeywords = StaticKeywords(shader, r);
                if (staticKeywords.Any(x => !active.Contains(x))) continue;
                var all = ProgramKeywords(programByBlob, r);
                if (requiredDynamicKeyword != null && !all.Contains(requiredDynamicKeyword, StringComparer.Ordinal)) continue;
                if (requiredDynamicKeyword == null && (all.Contains("SHADOWS_CUBE", StringComparer.Ordinal) || all.Contains("SHADOWS_DEPTH", StringComparer.Ordinal))) continue;
                var dynamic = all.Where(x => !active.Contains(x) && !staticKeywords.Contains(x, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                choices.Add(new ProgramChoice { Ref = r, Program = program, DynamicKeywords = dynamic, StaticMatchCount = staticKeywords.Count });
            }
            return choices
                .OrderByDescending(x => x.StaticMatchCount)
                .ThenBy(x => x.DynamicKeywords.Length)
                .ThenBy(x => x.Ref.StageVariantIndex)
                .FirstOrDefault();
        }

        private static List<string> StaticKeywords(Shader shader, ShaderPassProgramRef r)
        {
            var names = shader.m_ParsedForm?.m_KeywordNames ?? Array.Empty<string>();
            var result = new List<string>();
            foreach (var indices in new[] { r.KeywordIndices, r.LocalKeywordIndices })
            {
                if (indices == null) continue;
                foreach (var index in indices) if (index < names.Length) result.Add(names[index]);
            }
            return result.Distinct(StringComparer.Ordinal).ToList();
        }

        private static string[] ProgramKeywords(Dictionary<int, ShaderConverter.ExtractedShaderProgram> programByBlob, ShaderPassProgramRef r)
        {
            if (!programByBlob.TryGetValue((int)r.BlobIndex, out var p)) return Array.Empty<string>();
            return (p.Keywords ?? Array.Empty<string>()).Concat(p.LocalKeywords ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        }

        private static object ShaderStateDto(SerializedShaderState s)
        {
            if (s == null) return null;
            object FV(SerializedShaderFloatValue v, float fallback = 0f) => v == null ? fallback : string.IsNullOrEmpty(v.name) ? v.val : new { property = v.name, @default = v.val };
            object[] Blend(SerializedShaderRTBlendState b) => new[] { FV(b.srcBlend), FV(b.destBlend), FV(b.srcBlendAlpha), FV(b.destBlendAlpha), FV(b.blendOp), FV(b.blendOpAlpha), FV(b.colMask) };
            object[] Stencil(SerializedStencilOp st) => new[] { FV(st.pass), FV(st.fail), FV(st.zFail), FV(st.comp) };
            var tags = s.m_Tags?.tags?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>();
            return new
            {
                blend_targets = s.rtBlend?.Select(Blend).ToArray(), separate_blend = s.rtSeparateBlend,
                z_clip = FV(s.zClip, 1), z_test = FV(s.zTest, 4), z_write = FV(s.zWrite, 1), cull = FV(s.culling, 2),
                conservative = FV(s.conservative), offset_factor = FV(s.offsetFactor), offset_units = FV(s.offsetUnits), alpha_to_mask = FV(s.alphaToMask),
                stencil = new[] { Stencil(s.stencilOp), Stencil(s.stencilOpFront), Stencil(s.stencilOpBack) },
                stencil_read_mask = FV(s.stencilReadMask, 255), stencil_write_mask = FV(s.stencilWriteMask, 255), stencil_ref = FV(s.stencilRef),
                fog_start = FV(s.fogStart), fog_end = FV(s.fogEnd), fog_density = FV(s.fogDensity),
                fog_color = s.fogColor == null ? new float[] { 0, 0, 0, 0 } : new[] { s.fogColor.x.val, s.fogColor.y.val, s.fogColor.z.val, s.fogColor.w.val },
                fog_mode = (int)s.fogMode, tags, lod = s.m_LOD, lighting = s.lighting, fog_color_property = s.fogColor?.name
            };
        }

        private static string SafeShaderPath(string shaderName)
        {
            return string.Join(Path.DirectorySeparatorChar, shaderName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(Fix));
        }

        private static string ProgramExtension(ShaderGpuProgramType type)
        {
            return type switch
            {
                ShaderGpuProgramType.DX9VertexSM20 or ShaderGpuProgramType.DX9VertexSM30 or
                ShaderGpuProgramType.DX9PixelSM20 or ShaderGpuProgramType.DX9PixelSM30 or
                ShaderGpuProgramType.DX10Level9Vertex or ShaderGpuProgramType.DX10Level9Pixel or
                ShaderGpuProgramType.DX11VertexSM40 or ShaderGpuProgramType.DX11VertexSM50 or
                ShaderGpuProgramType.DX11PixelSM40 or ShaderGpuProgramType.DX11PixelSM50 or
                ShaderGpuProgramType.DX11GeometrySM40 or ShaderGpuProgramType.DX11GeometrySM50 or
                ShaderGpuProgramType.DX11HullSM50 or ShaderGpuProgramType.DX11DomainSM50 => ".dxbc",
                ShaderGpuProgramType.SPIRV => ".spv",
                ShaderGpuProgramType.GLLegacy or ShaderGpuProgramType.GLES31AEP or
                ShaderGpuProgramType.GLES31 or ShaderGpuProgramType.GLES3 or ShaderGpuProgramType.GLES or
                ShaderGpuProgramType.GLCore32 or ShaderGpuProgramType.GLCore41 or ShaderGpuProgramType.GLCore43 => ".glslbin",
                ShaderGpuProgramType.MetalVS or ShaderGpuProgramType.MetalFS => ".metalbin",
                _ => ".bin"
            };
        }

        private static List<ShaderPassProgramRef> BuildPassProgramMap(Shader shader)
        {
            var result = new List<ShaderPassProgramRef>();
            if (shader?.m_ParsedForm?.m_SubShaders == null) return result;
            for (var subShaderIndex = 0; subShaderIndex < shader.m_ParsedForm.m_SubShaders.Count; subShaderIndex++)
            {
                var subShader = shader.m_ParsedForm.m_SubShaders[subShaderIndex];
                if (subShader?.m_Passes == null) continue;
                for (var passIndex = 0; passIndex < subShader.m_Passes.Count; passIndex++)
                {
                    var pass = subShader.m_Passes[passIndex];
                    AddStageProgramRefs(result, subShaderIndex, passIndex, pass, "vertex", pass.progVertex);
                    AddStageProgramRefs(result, subShaderIndex, passIndex, pass, "fragment", pass.progFragment);
                    AddStageProgramRefs(result, subShaderIndex, passIndex, pass, "geometry", pass.progGeometry);
                    AddStageProgramRefs(result, subShaderIndex, passIndex, pass, "hull", pass.progHull);
                    AddStageProgramRefs(result, subShaderIndex, passIndex, pass, "domain", pass.progDomain);
                    AddStageProgramRefs(result, subShaderIndex, passIndex, pass, "raytracing", pass.progRayTracing);
                }
            }
            return result;
        }

        private static void AddStageProgramRefs(List<ShaderPassProgramRef> result, int subShaderIndex, int passIndex, SerializedPass pass, string stage, SerializedProgram program)
        {
            if (program?.m_SubPrograms == null) return;
            for (var i = 0; i < program.m_SubPrograms.Count; i++)
            {
                var sub = program.m_SubPrograms[i];
                if (sub == null) continue;
                result.Add(new ShaderPassProgramRef
                {
                    SubShaderIndex = subShaderIndex, PassIndex = passIndex, PassName = pass?.m_Name, UseName = pass?.m_UseName,
                    Stage = stage, StageVariantIndex = i, BlobIndex = sub.m_BlobIndex, GpuProgramType = sub.m_GpuProgramType.ToString(),
                    HardwareTier = sub.m_ShaderHardwareTier, BindChannels = sub.m_Channels, KeywordIndices = sub.m_KeywordIndices,
                    GlobalKeywordIndices = sub.m_GlobalKeywordIndices, LocalKeywordIndices = sub.m_LocalKeywordIndices
                });
            }
        }

        private static void ExportComponent(Context ctx, Component component)
        {
            if (component is Transform || component is MeshFilter) return;
            if (component is not Renderer && component is not MonoBehaviour && component is not Animator) return;

            if (component is Renderer renderer)
                CaptureRendererMaterialDependency(ctx, renderer);

            var key = Key(component);
            var nodePath = ctx.HierarchyNames.TryGetValue(key, out var ownerPath) ? ownerPath : component.Name;
            var nodeId = ctx.ComponentNodes.TryGetValue(key, out var knownNode) ? knownNode : "node:" + nodePath;
            var componentId = ctx.ComponentIds.TryGetValue(key, out var knownId) ? knownId : $"component:{nodePath}/{component.GetType().Name}#1";
            var ownerName = nodePath?.Split('/').LastOrDefault() ?? "node";
            var ordinal = componentId.Contains('#') ? componentId[(componentId.LastIndexOf('#') + 1)..] : "1";
            var fileName = UniqueFile(ctx, Fix(ownerName) + "__" + component.GetType().Name + "__" + ordinal, ".json", "Runtime/components");
            var relativeFile = "components/" + fileName;
            var output = Path.Combine(Dir(ctx, "Runtime/components"), fileName);

            object data;
            if (component is MonoBehaviour mb)
            {
                if (!Studio.assemblyLoader.Loaded)
                {
                    ctx.Manifest.Errors.Add($"DummyDll folder was not loaded; cannot decode MonoBehaviour {Describe(mb)}.");
                    data = new { decoded = false, script = PointerDto(mb.m_Script) };
                }
                else
                {
                    var typeTree = mb.ConvertToTypeTree(Studio.assemblyLoader);
                    if (typeTree == null)
                    {
                        ctx.Manifest.Errors.Add($"DummyDll could not resolve MonoBehaviour type for {Describe(mb)}.");
                        data = new { decoded = false, script = PointerDto(mb.m_Script) };
                    }
                    else
                    {
                        var decoded = mb.ToType(typeTree);
                        data = NormalizeRuntimeValue(ctx, mb, decoded, 0);
                        foreach (var ptr in GetPointers(decoded, mb))
                        {
                            if (!ResolveEdge(ctx, mb, ptr)) break;
                        }
                    }
                }
            }
            else if (component is Renderer r)
            {
                data = new
                {
                    m_Materials = r.m_Materials?.Select(p => RuntimeReference(ctx, component, p)).ToArray(),
                    m_Enabled = TryReadPublicMember(component, "m_Enabled"),
                    Name = component.Name
                };
            }
            else // Animator
            {
                data = new
                {
                    m_Avatar = TryReadPublicMember(component, "m_Avatar"),
                    m_Controller = TryReadPublicMember(component, "m_Controller"),
                    m_HasTransformHierarchy = TryReadPublicMember(component, "m_HasTransformHierarchy"),
                    m_Enabled = TryReadPublicMember(component, "m_Enabled"),
                    Name = component.Name
                };
            }

            WriteJson(output, new
            {
                id = componentId,
                type = component.GetType().Name,
                node = nodeId,
                data,
                unity = Identity(component)
            });
            ctx.RuntimeEntries.Add(new { id = componentId, type = component.GetType().Name, node = nodeId, file = relativeFile });
        }

        private static object RuntimeReference<T>(Context ctx, UnityObject owner, PPtr<T> ptr) where T : UnityObject
        {
            if (ptr == null || ptr.IsNull) return null;
            if (!ptr.TryGet(out T target))
                return new { unresolved = true, file_id = ptr.m_FileID, path_id = ptr.m_PathID, target_cab = ptr.GetTargetFileName() };
            return StableReference(ctx, target);
        }

        private static object StableReference(Context ctx, UnityObject target)
        {
            if (target == null) return null;
            var key = Key(target);
            if (target is Material material)
            {
                var path = MaterialRelativePath(ctx, material);
                return new { @ref = "material:" + material.Name, path, Name = material.Name };
            }
            if (target is Cubemap cubemap)
            {
                var path = CubemapRelativePath(ctx, cubemap);
                return new { @ref = "cubemap:" + cubemap.Name, path, Name = cubemap.Name };
            }
            if (target is Texture2D texture)
            {
                var path = TextureRelativePath(ctx, texture);
                return new { @ref = "texture:" + texture.Name, path, Name = texture.Name };
            }
            if (target is Component component)
            {
                if (component is Transform transform && transform.m_GameObject.TryGet(out var go) && ctx.HierarchyNames.TryGetValue(Key(go), out var transformPath))
                    return new { @ref = "node:" + transformPath, unity_type = "Transform" };
                if (ctx.ComponentIds.TryGetValue(key, out var componentId)) return new { @ref = componentId };
            }
            if (target is GameObject gameObject && ctx.HierarchyNames.TryGetValue(Key(gameObject), out var nodePath))
                return new { @ref = "node:" + nodePath };
            return new { unity = Identity(target) };
        }

        private static object NormalizeRuntimeValue(Context ctx, UnityObject owner, object value, int depth)
        {
            if (value == null || depth > 32) return value;
            if (value is string || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal || value.GetType().IsEnum)
                return value;
            if (value is byte[]) return value;

            if (value is OrderedDictionary dict)
            {
                if (TryDictionaryPPtr(dict, owner, "runtime", out var ptr))
                {
                    var p = new PPtr<UnityObject>(ptr.FileID, ptr.PathID, owner.assetsFile);
                    return p.TryGet(out var target) ? StableReference(ctx, target) : new { unresolved = true, file_id = ptr.FileID, path_id = ptr.PathID, target_cab = ptr.TargetCAB };
                }
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dict)
                    result[entry.Key?.ToString() ?? string.Empty] = NormalizeRuntimeValue(ctx, owner, entry.Value, depth + 1);
                return result;
            }

            if (value is IDictionary genericDict)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in genericDict)
                    result[entry.Key?.ToString() ?? string.Empty] = NormalizeRuntimeValue(ctx, owner, entry.Value, depth + 1);
                return result;
            }

            if (value is IEnumerable enumerable)
            {
                var list = new List<object>();
                foreach (var item in enumerable) list.Add(NormalizeRuntimeValue(ctx, owner, item, depth + 1));
                return list;
            }
            return value;
        }

        private static object TryReadPublicMember(object value, string name)
        {
            if (value == null) return null;
            var type = value.GetType();
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) return field.GetValue(value);
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(value);
        }

        private static void WriteHierarchy(Context ctx, IReadOnlyList<GameObject> roots)
        {
            var nodes = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots) AppendHierarchyNode(ctx, root, null, nodes, seen);
            var rootRef = roots.Count == 1 && ctx.HierarchyNames.TryGetValue(Key(roots[0]), out var rootPath) ? "node:" + rootPath : null;
            WriteJson(Path.Combine(Dir(ctx, "Runtime"), "hierarchy.json"), new
            {
                root = rootRef,
                support_parent = (string)null,
                nodes,
                support_nodes = Array.Empty<object>()
            });
        }

        private static void AppendHierarchyNode(Context ctx, GameObject go, string parent, List<object> nodes, HashSet<string> seen)
        {
            if (go == null || !seen.Add(Key(go))) return;
            var path = ctx.HierarchyNames.TryGetValue(Key(go), out var p) ? p : go.Name;
            var nodeId = "node:" + path;
            var transform = go.m_Transform;
            var componentIds = go.m_Components == null ? Array.Empty<string>() : go.m_Components
                .Select(ptr => ptr.TryGet(out Component c) && ctx.ComponentIds.TryGetValue(Key(c), out var id) ? id : null)
                .Where(x => x != null).ToArray();
            nodes.Add(new
            {
                id = nodeId,
                name = go.Name,
                parent,
                local_position = transform?.m_LocalPosition,
                local_rotation = transform?.m_LocalRotation,
                local_scale = transform?.m_LocalScale,
                components = componentIds,
                unity = new { cab = go.assetsFile?.fileName, gameobject_path_id = go.m_PathID, transform_path_id = transform?.m_PathID }
            });
            if (transform?.m_Children == null) return;
            foreach (var childPtr in transform.m_Children)
                if (childPtr.TryGet(out var childTransform) && childTransform.m_GameObject.TryGet(out var childGo))
                    AppendHierarchyNode(ctx, childGo, nodeId, nodes, seen);
        }

        private static void WriteRuntimeManifest(Context ctx)
        {
            WriteJson(Path.Combine(Dir(ctx, "Runtime"), "runtime.json"), new
            {
                hierarchy = "hierarchy.json",
                components = ctx.RuntimeEntries,
                objects = Array.Empty<object>(),
                external_assets = Array.Empty<object>()
            });
        }

        private static void CleanupRedundantDefaultMaterial(Context ctx)
        {
            var authoritative = new HashSet<string>(ctx.Manifest.RendererMaterialDependencies
                .SelectMany(r => r.Materials)
                .Where(x => x.Resolved && !string.IsNullOrWhiteSpace(x.MaterialName))
                .Select(x => x.MaterialName), StringComparer.OrdinalIgnoreCase);
            if (authoritative.Contains("Avatar_Default_Mat")) return;
            var path = Path.Combine(ctx.Root, "Materials", "Avatar_Default_Mat.json");
            if (File.Exists(path)) File.Delete(path);
        }

        private static string[] GetMaterialKeywords(Material mat)
        {
            var result = new List<string>();
            switch (mat.m_ShaderKeywords)
            {
                case string s:
                    result.AddRange(s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                    break;
                case string[] a:
                    result.AddRange(a);
                    break;
                case IEnumerable<string> e:
                    result.AddRange(e);
                    break;
            }
            if (mat.m_ValidKeywords != null) result.AddRange(mat.m_ValidKeywords);
            // Unity keeps enabled-but-currently-invalid keywords separately in
            // 2021.3+. They still describe the serialized material variant and
            // must not be silently discarded during shader selection.
            if (mat.m_InvalidKeywords != null) result.AddRange(mat.m_InvalidKeywords);
            return result.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
        }

        private static void MergeDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static IEnumerable<PointerInfo> GetPointers(UnityObject owner)
        {
            if (owner is Material material)
            {
                var direct = new List<PointerInfo>();
                if (material.m_Shader != null)
                {
                    var p = material.m_Shader;
                    direct.Add(new PointerInfo { Property = "Material.m_Shader", FileID = p.m_FileID, PathID = p.m_PathID, TargetCAB = p.GetTargetFileName() });
                }
                if (material.m_SavedProperties?.m_TexEnvs != null)
                {
                    foreach (var kv in material.m_SavedProperties.m_TexEnvs)
                    {
                        var p = kv.Value?.m_Texture;
                        if (p == null) continue;
                        direct.Add(new PointerInfo
                        {
                            Property = $"Material.m_TexEnvs[{kv.Key}]",
                            FileID = p.m_FileID, PathID = p.m_PathID, TargetCAB = p.GetTargetFileName()
                        });
                    }
                }
                return direct;
            }
            if (owner is ParticleSystemRenderer particleRenderer)
            {
                var direct = GetPointers((object)owner, owner).ToList();
                var seen = new HashSet<string>(direct.Select(x => x.FileID + "|" + x.PathID));
                byte[] raw = null;
                try { raw = particleRenderer.GetRawData(); } catch { }
                if (raw != null)
                {
                    var start = Math.Max(0, particleRenderer.m_DerivedDataOffset);
                    for (var offset = start; offset + 12 <= raw.Length; offset += 4)
                    {
                        var fileId = BitConverter.ToInt32(raw, offset);
                        if (fileId < 0 || particleRenderer.assetsFile == null || fileId > particleRenderer.assetsFile.m_Externals.Count)
                            continue;
                        var pathId = BitConverter.ToInt64(raw, offset + 4);
                        if (pathId == 0) continue;
                        var key = fileId + "|" + pathId;
                        if (seen.Contains(key)) continue;
                        var candidate = new PPtr<UnityObject>(fileId, pathId, particleRenderer.assetsFile);
                        if (candidate.TryGet(out var target) && target is Mesh)
                        {
                            seen.Add(key);
                            direct.Add(new PointerInfo
                            {
                                Property = $"ParticleSystemRenderer.derivedMeshPPtr@0x{offset:X}",
                                FileID = fileId, PathID = pathId, TargetCAB = candidate.GetTargetFileName(), Heuristic = true
                            });
                        }
                    }
                }
                return direct;
            }
            if (owner is Cubemap cubemap)
            {
                var direct = new List<PointerInfo>();
                if (cubemap.m_SourceTextures != null)
                    for (var i = 0; i < cubemap.m_SourceTextures.Count; i++)
                    {
                        var p = cubemap.m_SourceTextures[i];
                        direct.Add(new PointerInfo { Property = $"Cubemap.m_SourceTextures[{i}]", FileID = p.m_FileID, PathID = p.m_PathID, TargetCAB = p.GetTargetFileName() });
                    }
                return direct;
            }
            if (owner is Shader shader)
            {
                var direct = new List<PointerInfo>();
                if (shader.m_Dependencies != null)
                    for (var i = 0; i < shader.m_Dependencies.Count; i++)
                    {
                        var p = shader.m_Dependencies[i];
                        direct.Add(new PointerInfo { Property = $"Shader.m_Dependencies[{i}]", FileID = p.m_FileID, PathID = p.m_PathID, TargetCAB = p.GetTargetFileName() });
                    }
                if (shader.m_NonModifiableTextures != null)
                    foreach (var kv in shader.m_NonModifiableTextures)
                    {
                        var p = kv.Value;
                        direct.Add(new PointerInfo { Property = $"Shader.m_NonModifiableTextures[{kv.Key}]", FileID = p.m_FileID, PathID = p.m_PathID, TargetCAB = p.GetTargetFileName() });
                    }
                return direct;
            }
            return GetPointers((object)owner, owner);
        }

        private static IEnumerable<PointerInfo> GetPointers(object root, UnityObject owner)
        {
            var result = new List<PointerInfo>();
            var visited = new HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            Scan(root, owner, owner.GetType().Name, 0, visited, result);
            return result;
        }

        private static void Scan(object value, UnityObject owner, string path, int depth, HashSet<object> visited, List<PointerInfo> result)
        {
            if (value == null || depth > 12) return;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PPtr<>))
            {
                var fileId = (int)type.GetField("m_FileID").GetValue(value);
                var pathId = (long)type.GetField("m_PathID").GetValue(value);
                string target = null;
                var method = type.GetMethod("GetTargetFileName");
                if (method != null) target = method.Invoke(value, null) as string;
                result.Add(new PointerInfo { Property = path, FileID = fileId, PathID = pathId, TargetCAB = target });
                return;
            }

            if (value is UnityObject && !ReferenceEquals(value, owner)) return;
            if (!type.IsValueType && !visited.Add(value)) return;

            if (value is OrderedDictionary ordered)
            {
                if (TryDictionaryPPtr(ordered, owner, path, out var decodedPtr))
                {
                    result.Add(decodedPtr);
                    return;
                }
                foreach (DictionaryEntry e in ordered) Scan(e.Value, owner, path + "." + e.Key, depth + 1, visited, result);
                return;
            }
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry e in dictionary) Scan(e.Value, owner, path + "." + e.Key, depth + 1, visited, result);
                return;
            }
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                if (elementType != null && (elementType.IsPrimitive || elementType.IsEnum || elementType == typeof(string))) return;
            }
            if (value is IEnumerable enumerable && value is not byte[])
            {
                var i = 0;
                foreach (var item in enumerable) Scan(item, owner, path + "[" + i++ + "]", depth + 1, visited, result);
                return;
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                // Never recurse into Object.assetsFile/reader/version/etc. Those are parser/runtime
                // infrastructure, not serialized object dependencies, and would explode closure into
                // the entire loaded corpus. Parsed subclasses expose their real PPtrs as public fields.
                if (field.DeclaringType == typeof(UnityObject)) continue;
                Scan(field.GetValue(value), owner, path + "." + field.Name, depth + 1, visited, result);
            }
        }

        private static bool TryDictionaryPPtr(OrderedDictionary dict, UnityObject owner, string path, out PointerInfo result)
        {
            result = null;
            object fileValue = null;
            object pathValue = null;
            foreach (DictionaryEntry e in dict)
            {
                var key = e.Key?.ToString();
                if (string.Equals(key, "m_FileID", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "fileID", StringComparison.OrdinalIgnoreCase)) fileValue = e.Value;
                if (string.Equals(key, "m_PathID", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "pathID", StringComparison.OrdinalIgnoreCase)) pathValue = e.Value;
            }
            if (fileValue == null || pathValue == null) return false;
            try
            {
                var fileId = Convert.ToInt32(fileValue);
                var pathId = Convert.ToInt64(pathValue);
                string target = null;
                if (fileId == 0) target = owner.assetsFile?.fileName;
                else if (fileId > 0 && owner.assetsFile != null && fileId - 1 < owner.assetsFile.m_Externals.Count)
                    target = owner.assetsFile.m_Externals[fileId - 1].fileName;
                result = new PointerInfo { Property = path, FileID = fileId, PathID = pathId, TargetCAB = target };
                return true;
            }
            catch { return false; }
        }

        private static void CaptureRendererMaterialDependency(Context ctx, Renderer renderer)
        {
            if (renderer == null) return;
            var sourceFile = renderer.assetsFile?.fileName ?? string.Empty;
            var originalPath = renderer.assetsFile?.originalPath ?? string.Empty;
            if (ctx.Manifest.RendererMaterialDependencies.Any(x =>
                    x.RendererPathID == renderer.m_PathID &&
                    string.Equals(x.RendererSourceFile ?? string.Empty, sourceFile, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.RendererOriginalPath ?? string.Empty, originalPath, StringComparison.OrdinalIgnoreCase)))
                return;

            renderer.m_GameObject.TryGet(out var gameObject);
            string transformPath = null;
            if (gameObject != null)
            {
                if (!ctx.HierarchyNames.TryGetValue(Key(gameObject), out transformPath))
                    transformPath = gameObject.Name;
            }

            var dependency = new RendererMaterialDependency
            {
                RendererType = renderer.GetType().Name,
                RendererPathID = renderer.m_PathID,
                RendererSourceFile = sourceFile,
                RendererOriginalPath = originalPath,
                GameObjectName = gameObject?.m_Name ?? string.Empty,
                GameObjectPathID = gameObject?.m_PathID,
                GameObjectSourceFile = gameObject?.assetsFile?.fileName ?? string.Empty,
                GameObjectOriginalPath = gameObject?.assetsFile?.originalPath ?? string.Empty,
                TransformPath = transformPath
            };

            if (renderer.m_Materials != null)
            {
                for (var slotIndex = 0; slotIndex < renderer.m_Materials.Count; slotIndex++)
                {
                    var materialPtr = renderer.m_Materials[slotIndex];
                    var slot = new RendererMaterialSlotDependency
                    {
                        SlotIndex = slotIndex,
                        FileID = materialPtr.m_FileID,
                        PathID = materialPtr.m_PathID,
                        IsNull = materialPtr.IsNull
                    };
                    if (materialPtr.TryGet(out var material))
                    {
                        slot.Resolved = true;
                        slot.MaterialName = material.m_Name;
                        slot.ResolvedSourceFile = material.assetsFile?.fileName ?? string.Empty;
                        slot.ResolvedOriginalPath = material.assetsFile?.originalPath ?? string.Empty;
                        slot.ResolvedPathID = material.m_PathID;
                        Enqueue(ctx, material);
                    }
                    dependency.Materials.Add(slot);
                }
            }
            ctx.Manifest.RendererMaterialDependencies.Add(dependency);
        }

        private static void ValidateRendererDependencies(Context ctx)
        {
            foreach (var renderer in ctx.Manifest.RendererMaterialDependencies)
            {
                foreach (var slot in renderer.Materials)
                {
                    if (!slot.IsNull && !slot.Resolved)
                        ctx.Manifest.Errors.Add($"Renderer material dependency unresolved: {renderer.TransformPath} ({renderer.RendererType}) slot {slot.SlotIndex}, FileID={slot.FileID}, PathID={slot.PathID}.");
                }
            }
        }

        private static object PointerDto<T>(PPtr<T> ptr) where T : UnityObject
        {
            if (ptr == null) return null;
            return new { ptr.m_FileID, ptr.m_PathID, IsNull = ptr.IsNull, SourceCAB = ptr.GetSourceFileName(), TargetCAB = ptr.GetTargetFileName() };
        }

        private static ObjectIdentity Identity(UnityObject obj) => new()
        {
            Type = obj.type.ToString(), Name = obj.Name,
            SourceCAB = obj.assetsFile?.fileName, OriginalPath = obj.assetsFile?.originalPath, PathID = obj.m_PathID
        };

        private static string Key(UnityObject obj) => (obj.assetsFile?.fileName ?? "") + "|" + (obj.assetsFile?.originalPath ?? "") + "|" + obj.m_PathID;
        private static string Describe(UnityObject obj) => $"{obj.type}:{obj.Name} [{obj.assetsFile?.fileName}:{obj.m_PathID}]";

        private static string MaterialRelativePath(Context ctx, Material material)
        {
            var key = Key(material);
            if (ctx.MaterialPaths.TryGetValue(key, out var path)) return path;
            var stem = UniqueStem(ctx, Fix(string.IsNullOrWhiteSpace(material.Name) ? "Material" : material.Name), "Materials");
            path = "Materials/" + stem + ".json";
            ctx.MaterialPaths[key] = path;
            return path;
        }

        private static string TextureRelativePath(Context ctx, Texture2D texture)
        {
            var key = Key(texture);
            if (ctx.TexturePaths.TryGetValue(key, out var path)) return path;
            var stem = UniqueStem(ctx, Fix(string.IsNullOrWhiteSpace(texture.Name) ? "Texture" : texture.Name), "Textures");
            path = stem + ".png";
            ctx.TexturePaths[key] = path;
            return path;
        }

        private static string CubemapRelativePath(Context ctx, Cubemap cubemap)
        {
            var key = Key(cubemap);
            if (ctx.CubemapPaths.TryGetValue(key, out var path)) return path;
            var stem = UniqueStem(ctx, Fix(string.IsNullOrWhiteSpace(cubemap.Name) ? "Cubemap" : cubemap.Name), "Cubemaps");
            path = $"Cubemaps/{stem}/{stem}.json";
            ctx.CubemapPaths[key] = path;
            return path;
        }

        private static string Dir(Context ctx, string name)
        {
            var p = Path.Combine(ctx.Root, name); Directory.CreateDirectory(p); return p;
        }

        private static string UniqueStem(Context ctx, string stem, string scope = "")
        {
            if (string.IsNullOrWhiteSpace(stem)) stem = "unnamed";
            var key = scope + "|" + stem;
            if (!ctx.FileNameCounts.TryGetValue(key, out var count)) { ctx.FileNameCounts[key] = 1; return stem; }
            count++; ctx.FileNameCounts[key] = count; return stem + "__" + count;
        }

        private static string UniqueFile(Context ctx, string stem, string ext, string scope = "") => UniqueStem(ctx, stem, scope) + ext;

        private static string Fix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(bad.Contains(c) || c == '/' || c == '\\' ? '_' : c);
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "unnamed" : result;
        }


        private static string SHA256File(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string SHA256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private static void Count(Context ctx, string type)
        {
            ctx.Manifest.ObjectCounts.TryGetValue(type, out var n); ctx.Manifest.ObjectCounts[type] = n + 1;
        }

        private static void WriteJson(string path, object value)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                NullValueHandling = NullValueHandling.Include
            };
            settings.Converters.Add(new StringEnumConverter());
            File.WriteAllText(path, JsonConvert.SerializeObject(value, settings));
        }
    }
}
