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
            public List<PackageFile> Files = new();
        }

        private sealed class PackageFile
        {
            public string Path;
            public long Size;
            public string SHA256;
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

        private sealed class MeshFile
        {
            public ObjectIdentity Identity;
            public int VertexCount;
            public int SubMeshCount;
            public bool HasNormals;
            public bool HasTangents;
            public bool HasColors;
            public bool[] UV = new bool[8];
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
            public Manifest Manifest;
            public readonly Queue<UnityObject> Queue = new();
            public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> HierarchyNames = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> FileNameCounts = new(StringComparer.OrdinalIgnoreCase);
            public int EdgeCount;
        }

        public static void Export(ModelConverter converter, string fbxPath)
        {
            if (converter == null || converter.RootGameObjects.Count == 0) return;

            var modelName = Path.GetFileNameWithoutExtension(fbxPath);
            var finalRoot = Path.Combine(Path.GetDirectoryName(fbxPath) ?? string.Empty, modelName + ".resolved");
            var tempRoot = finalRoot + ".tmp";
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
            var ctx = new Context { Root = tempRoot, Manifest = manifest };

            try
            {
                if (!manifest.ResolveDependenciesEnabled)
                    manifest.Errors.Add("AnimeStudio 'Resolve dependencies' is disabled. Reload the source corpus with dependency resolution enabled.");
                if (!manifest.CABMapLoaded)
                    manifest.Errors.Add("No CAB map is loaded. A resolved package cannot prove contextual external PPtrs without the Genshin CAB map.");

                var modelDir = Dir(ctx, "Model");
                File.Copy(fbxPath, Path.Combine(modelDir, Path.GetFileName(fbxPath)), true);
                var modelMetadata = Path.ChangeExtension(fbxPath, ".model-metadata.json");
                if (File.Exists(modelMetadata)) File.Copy(modelMetadata, Path.Combine(modelDir, Path.GetFileName(modelMetadata)), true);

                foreach (var root in converter.RootGameObjects)
                    CollectHierarchy(ctx, root, Fix(root.Name));

                ProcessQueue(ctx);
                ValidateRendererDependencies(ctx);

                manifest.DummyDllLoaded = Studio.assemblyLoader.Loaded;
                manifest.DummyDllAssemblyCount = Studio.assemblyLoader.LoadedAssemblyCount;
                manifest.ValidationStatus = manifest.Errors.Count == 0 ? "VALIDATION_OK" : "VALIDATION_FAILED";
                manifest.Files = BuildInventory(tempRoot);
                WriteJson(Path.Combine(tempRoot, "manifest.json"), manifest);
                File.WriteAllLines(Path.Combine(tempRoot, manifest.Errors.Count == 0 ? "VALIDATION_OK.txt" : "VALIDATION_FAILED.txt"),
                    manifest.Errors.Count == 0 ? new[] { "Resolved model package validation passed." } : manifest.Errors);
            }
            catch (Exception ex)
            {
                manifest.Errors.Add("Exporter exception: " + ex);
                manifest.ValidationStatus = "VALIDATION_FAILED";
                WriteJson(Path.Combine(tempRoot, "manifest.json"), manifest);
                File.WriteAllLines(Path.Combine(tempRoot, "VALIDATION_FAILED.txt"), manifest.Errors);
            }

            if (Directory.Exists(finalRoot)) Directory.Delete(finalRoot, true);
            Directory.Move(tempRoot, finalRoot);

            if (manifest.Errors.Count > 0)
                throw new InvalidOperationException($"Resolved package validation failed for {modelName}. See {Path.Combine(finalRoot, "VALIDATION_FAILED.txt")}");

            Logger.Info($"Resolved package: {finalRoot}");
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
                        ctx.HierarchyNames[Key(component)] = path + "__" + component.GetType().Name + "__" + ordinal;
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
                case Shader shader: ExportShader(ctx, shader); break;
                case Mesh mesh: ExportMesh(ctx, mesh); break;
                case Component component: ExportComponent(ctx, component); break;
                case GameObject gameObject: ExportGameObjectMetadata(ctx, gameObject); break;
                default: ExportDependency(ctx, obj); break;
            }
        }

        private static void ExportMaterial(Context ctx, Material mat)
        {
            var file = new MaterialFile
            {
                Identity = Identity(mat),
                Shader = PointerDto(mat.m_Shader),
                ShaderKeywords = mat.m_ShaderKeywords,
                ValidKeywords = mat.m_ValidKeywords,
                InvalidKeywords = mat.m_InvalidKeywords,
                LightmapFlags = mat.m_LightmapFlags,
                EnableInstancingVariants = mat.m_EnableInstancingVariants,
                DoubleSidedGI = mat.m_DoubleSidedGI,
                CustomRenderQueue = mat.m_CustomRenderQueue,
                StringTagMap = mat.m_StringTagMap,
                DisabledShaderPasses = mat.m_DisabledShaderPasses,
                Ints = mat.m_SavedProperties?.m_Ints,
                Floats = mat.m_SavedProperties?.m_Floats,
                Colors = mat.m_SavedProperties?.m_Colors
            };
            if (mat.m_SavedProperties?.m_TexEnvs != null)
            {
                foreach (var kv in mat.m_SavedProperties.m_TexEnvs)
                {
                    var env = kv.Value;
                    file.TextureBindings.Add(new { Name = kv.Key, Texture = PointerDto(env.m_Texture), env.m_Scale, env.m_Offset });
                }
            }
            WriteJson(Path.Combine(Dir(ctx, "Materials"), UniqueFile(ctx, Fix(mat.Name), ".json", "Materials")), file);
            WriteRawProvenance(ctx, mat);
        }

        private static void ExportTexture(Context ctx, Texture2D tex)
        {
            var baseName = UniqueStem(ctx, Fix(tex.Name), "Textures");
            var dir = Dir(ctx, "Textures");
            var payloadName = baseName + ".payload.bin";
            string sha = null;
            try
            {
                var data = tex.image_data.GetData();
                File.WriteAllBytes(Path.Combine(dir, payloadName), data);
                sha = SHA256Hex(data);
            }
            catch (Exception ex)
            {
                ctx.Manifest.Errors.Add($"Texture payload unavailable for {Describe(tex)}: {ex.Message}");
            }

            string converted = null;
            try
            {
                using var image = tex.ConvertToImage(true);
                if (image != null)
                {
                    converted = baseName + ".png";
                    using var fs = File.Create(Path.Combine(dir, converted));
                    image.WriteToStream(fs, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                ctx.Manifest.Warnings.Add($"Texture conversion failed for {Describe(tex)}; exact payload is still preserved: {ex.Message}");
            }

            WriteJson(Path.Combine(dir, baseName + ".texture.json"), new TextureFile
            {
                Identity = Identity(tex), Width = tex.m_Width, Height = tex.m_Height,
                CompleteImageSize = tex.m_CompleteImageSize, MipsStripped = tex.m_MipsStripped,
                TextureFormat = tex.m_TextureFormat.ToString(), MipMap = tex.m_MipMap, MipCount = tex.m_MipCount,
                IsReadable = tex.m_IsReadable, IsPreProcessed = tex.m_IsPreProcessed,
                IgnoreMasterTextureLimit = tex.m_IgnoreMasterTextureLimit, StreamingMipmaps = tex.m_StreamingMipmaps,
                StreamingMipmapsPriority = tex.m_StreamingMipmapsPriority, ImageCount = tex.m_ImageCount,
                TextureDimension = tex.m_TextureDimension, LightmapFormat = tex.m_LightmapFormat, ColorSpace = tex.m_ColorSpace,
                PlatformBlob = tex.m_PlatformBlob, ExternalMipRelativeOffset = tex.m_ExternalMipRelativeOffset,
                Sampler = tex.m_TextureSettings, Stream = tex.m_StreamData,
                ExactPayload = payloadName, ConvertedImage = converted, PayloadSHA256 = sha
            });
            WriteRawProvenance(ctx, tex);
        }

        private static void ExportCubemap(Context ctx, Cubemap cube)
        {
            var baseName = UniqueStem(ctx, Fix(cube.Name), "Cubemaps");
            var dir = Dir(ctx, "Cubemaps");
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
            WriteJson(Path.Combine(dir, baseName + ".cubemap.json"), new CubemapFile
            {
                Identity = Identity(cube), Width = cube.m_Width, Height = cube.m_Height,
                CompleteImageSize = cube.m_CompleteImageSize, TextureFormat = cube.m_TextureFormat.ToString(),
                MipMap = cube.m_MipMap, MipCount = cube.m_MipCount, IsReadable = cube.m_IsReadable,
                IsPreProcessed = cube.m_IsPreProcessed, IgnoreMasterTextureLimit = cube.m_IgnoreMasterTextureLimit,
                StreamingMipmaps = cube.m_StreamingMipmaps, StreamingMipmapsPriority = cube.m_StreamingMipmapsPriority,
                ImageCount = cube.m_ImageCount, TextureDimension = cube.m_TextureDimension,
                LightmapFormat = cube.m_LightmapFormat, ColorSpace = cube.m_ColorSpace,
                SourceTextures = cube.m_SourceTextures?.Select(x => PointerDto(x)).ToArray(),
                Sampler = cube.m_TextureSettings, Stream = cube.m_StreamData,
                ExactPayload = payloadName, PayloadSHA256 = sha
            });
            WriteRawProvenance(ctx, cube);
        }

        private static void ExportShader(Context ctx, Shader shader)
        {
            var shaderRoot = Path.Combine(Dir(ctx, "Shaders"), UniqueStem(ctx, Fix(string.IsNullOrEmpty(shader.Name) ? "Shader" : shader.Name), "Shaders"));
            Directory.CreateDirectory(shaderRoot);
            File.WriteAllBytes(Path.Combine(shaderRoot, "shader.raw.bin"), shader.GetRawData());

            if (shader.m_IsRawOnly || shader.m_ParsedForm == null)
            {
                ctx.Manifest.Errors.Add($"Referenced shader is raw-only/unparsed: {Describe(shader)}. Exact raw bytes were preserved but structured viewer semantics are incomplete.");
                WriteJson(Path.Combine(shaderRoot, "shader.json"), new { Identity = Identity(shader), RawOnly = true });
                return;
            }

            var passProgramMap = BuildPassProgramMap(shader);
            WriteJson(Path.Combine(shaderRoot, "shader.json"), new
            {
                Identity = Identity(shader), RawOnly = false, shader.m_ParsedForm,
                Platforms = shader.platforms, shader.stageCounts,
                PassProgramMap = passProgramMap,
                Dependencies = shader.m_Dependencies?.Select(x => PointerDto(x)).ToArray(),
                NonModifiableTextures = shader.m_NonModifiableTextures?.Select(x => new { Name = x.Key, Texture = PointerDto(x.Value) }).ToArray()
            });
            try
            {
                var converted = shader.Convert();
                if (!string.IsNullOrEmpty(converted)) File.WriteAllText(Path.Combine(shaderRoot, "shader.converted.txt"), converted);
            }
            catch (Exception ex)
            {
                ctx.Manifest.Warnings.Add($"Shader text conversion failed for {Describe(shader)}: {ex.Message}");
            }

            try
            {
                var programs = shader.ExtractPrograms();
                if (shader.compressedBlob != null && programs.Count == 0)
                    ctx.Manifest.Errors.Add($"Shader {Describe(shader)} has a serialized program blob but no exact GPU programs were extracted.");

                var programDir = Path.Combine(shaderRoot, "Programs");
                Directory.CreateDirectory(programDir);
                foreach (var p in programs)
                {
                    var extension = ProgramExtension(p.ProgramType);
                    var stem = $"{Fix(p.Platform.ToString())}__{Fix(p.ProgramType.ToString())}__blob{p.VariantIndex:D4}";
                    File.WriteAllBytes(Path.Combine(programDir, stem + extension), p.ProgramCode);
                    WriteJson(Path.Combine(programDir, stem + ".json"), new
                    {
                        p.PlatformIndex, p.Platform, BlobIndex = p.VariantIndex, p.ProgramType,
                        p.Keywords, p.LocalKeywords, Size = p.ProgramCode.Length, SHA256 = SHA256Hex(p.ProgramCode)
                    });
                }

                // Also expose the exact same program bytes through pass/stage-oriented paths.
                // This is the human-readable archive view; BlobIndex remains provenance, not the canonical name.
                foreach (var passRef in passProgramMap)
                {
                    var matchingPrograms = programs.Where(x => x.VariantIndex == (int)passRef.BlobIndex).ToList();
                    if (matchingPrograms.Count == 0)
                    {
                        ctx.Manifest.Errors.Add($"Shader pass program missing for {Describe(shader)}: subshader {passRef.SubShaderIndex}, pass {passRef.PassIndex}, stage {passRef.Stage}, blob {passRef.BlobIndex}.");
                        continue;
                    }

                    var passName = !string.IsNullOrWhiteSpace(passRef.PassName) ? passRef.PassName :
                                   !string.IsNullOrWhiteSpace(passRef.UseName) ? passRef.UseName : $"Pass{passRef.PassIndex:D2}";
                    var passDir = Path.Combine(shaderRoot, "Passes", $"SubShader{passRef.SubShaderIndex:D2}",
                        Fix(passName), Fix(passRef.Stage));
                    Directory.CreateDirectory(passDir);

                    foreach (var p in matchingPrograms)
                    {
                        var platformDir = Path.Combine(passDir, Fix(p.Platform.ToString()));
                        Directory.CreateDirectory(platformDir);
                        var stem = $"variant{passRef.StageVariantIndex:D3}__blob{passRef.BlobIndex:D4}";
                        var extension = ProgramExtension(p.ProgramType);
                        File.WriteAllBytes(Path.Combine(platformDir, stem + extension), p.ProgramCode);
                        WriteJson(Path.Combine(platformDir, stem + ".json"), new
                        {
                            passRef.SubShaderIndex, passRef.PassIndex, passRef.PassName, passRef.UseName,
                            passRef.Stage, passRef.StageVariantIndex, passRef.BlobIndex,
                            p.PlatformIndex, p.Platform, p.ProgramType,
                            passRef.HardwareTier, passRef.BindChannels, passRef.KeywordIndices,
                            passRef.GlobalKeywordIndices, passRef.LocalKeywordIndices,
                            p.Keywords, p.LocalKeywords, Size = p.ProgramCode.Length, SHA256 = SHA256Hex(p.ProgramCode)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                ctx.Manifest.Errors.Add($"Exact shader program extraction failed for {Describe(shader)}: {ex.Message}");
            }
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

        private static void ExportMesh(Context ctx, Mesh mesh)
        {
            var info = new MeshFile
            {
                Identity = Identity(mesh), VertexCount = mesh.m_VertexCount,
                SubMeshCount = mesh.m_SubMeshes?.Count ?? 0,
                HasNormals = mesh.m_Normals?.Length > 0,
                HasTangents = mesh.m_Tangents?.Length > 0,
                HasColors = mesh.m_Colors?.Length > 0,
                UV = new[] { mesh.m_UV0, mesh.m_UV1, mesh.m_UV2, mesh.m_UV3, mesh.m_UV4, mesh.m_UV5, mesh.m_UV6, mesh.m_UV7 }.Select(x => x?.Length > 0).ToArray()
            };
            WriteJson(Path.Combine(Dir(ctx, "Meshes"), UniqueFile(ctx, Fix(mesh.Name), ".mesh.json", "Meshes")), info);
        }

        private static void ExportComponent(Context ctx, Component component)
        {
            if (component is Renderer renderer)
                CaptureRendererMaterialDependency(ctx, renderer);

            var name = ctx.HierarchyNames.TryGetValue(Key(component), out var hierarchy)
                ? Fix(hierarchy.Replace('/', '_')) : Fix(component.Name);
            if (string.IsNullOrWhiteSpace(name)) name = component.GetType().Name;
            var file = Path.Combine(Dir(ctx, "Components"), UniqueFile(ctx, name, ".json", "Components"));

            if (component is MonoBehaviour mb)
            {
                if (!Studio.assemblyLoader.Loaded)
                {
                    ctx.Manifest.Errors.Add($"DummyDll folder was not loaded before export; cannot decode MonoBehaviour {Describe(mb)}. Use Export -> Load DummyDll folder for resolved model export first.");
                    WriteJson(file, new { Identity = Identity(mb), Decoded = false, Script = PointerDto(mb.m_Script) });
                    WriteRawProvenance(ctx, mb);
                    return;
                }
                var typeTree = mb.ConvertToTypeTree(Studio.assemblyLoader);
                if (typeTree == null)
                {
                    ctx.Manifest.Errors.Add($"DummyDll could not resolve MonoBehaviour type for {Describe(mb)}.");
                    WriteJson(file, new { Identity = Identity(mb), Decoded = false, Script = PointerDto(mb.m_Script) });
                    WriteRawProvenance(ctx, mb);
                    return;
                }
                var decoded = mb.ToType(typeTree);
                WriteJson(file, new { Identity = Identity(mb), Decoded = true, Data = decoded });
                WriteRawProvenance(ctx, mb);
                foreach (var ptr in GetPointers(decoded, mb))
                {
                    if (!ResolveEdge(ctx, mb, ptr)) break;
                }
                return;
            }

            // Parsed built-in component state is intentionally serialized here rather than reconstructed later.
            // ParticleSystemRenderer has version/game-specific derived fields that are not all represented by
            // AnimeStudio's Renderer base parser. When Unity type-tree data is available, preserve it too and
            // traverse its PPtrs so particle meshes/effect references are not silently missed.
            OrderedDictionary serializedData = null;
            if (component is ParticleSystemRenderer && component.serializedType?.m_Type != null)
            {
                try
                {
                    serializedData = component.ToType();
                    foreach (var ptr in GetPointers(serializedData, component))
                    {
                        if (!ResolveEdge(ctx, component, ptr)) break;
                    }
                }
                catch (Exception ex)
                {
                    ctx.Manifest.Warnings.Add($"ParticleSystemRenderer type-tree decode failed for {Describe(component)}: {ex.Message}");
                }
            }
            WriteJson(file, new { Identity = Identity(component), Data = component, SerializedData = serializedData });
            WriteRawProvenance(ctx, component);
        }

        private static void ExportGameObjectMetadata(Context ctx, GameObject go)
        {
            var hierarchy = ctx.HierarchyNames.TryGetValue(Key(go), out var p) ? p : go.Name;
            WriteJson(Path.Combine(Dir(ctx, "Hierarchy"), UniqueFile(ctx, Fix(hierarchy.Replace('/', '_')), ".gameobject.json", "Hierarchy")), new
            {
                Identity = Identity(go), HierarchyPath = hierarchy,
                Components = go.m_Components?.Select(x => PointerDto(x)).ToArray(), AnimatorPresent = go.m_Animator != null
            });
            WriteRawProvenance(ctx, go);
        }

        private static void ExportDependency(Context ctx, UnityObject obj)
        {
            var dir = Path.Combine(Dir(ctx, "Dependencies"), Fix(obj.type.ToString()));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, UniqueFile(ctx, Fix(string.IsNullOrEmpty(obj.Name) ? obj.type.ToString() : obj.Name), ".json", "Dependencies/" + obj.type));
            WriteJson(path, new { Identity = Identity(obj), Data = obj });
            if (obj.GetType() == typeof(UnityObject))
            {
                WriteRawProvenance(ctx, obj);
                ctx.Manifest.Warnings.Add($"Dependency {Describe(obj)} has no parsed AnimeStudio class; exact raw bytes kept under Provenance.");
            }
        }


        private static void WriteRawProvenance(Context ctx, UnityObject obj)
        {
            try
            {
                var rawDir = Path.Combine(Dir(ctx, "Provenance"), "Raw", Fix(obj.type.ToString()));
                Directory.CreateDirectory(rawDir);
                var stem = Fix(string.IsNullOrEmpty(obj.Name) ? obj.type.ToString() : obj.Name);
                File.WriteAllBytes(Path.Combine(rawDir, UniqueFile(ctx, stem, ".raw.bin", "Provenance/Raw/" + obj.type)), obj.GetRawData());
            }
            catch (Exception ex)
            {
                ctx.Manifest.Warnings.Add($"Could not preserve raw provenance for {Describe(obj)}: {ex.Message}");
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


        private static List<PackageFile> BuildInventory(string root)
        {
            var result = new List<PackageFile>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("VALIDATION_", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(file);
                result.Add(new PackageFile { Path = relative, Size = info.Length, SHA256 = SHA256File(file) });
            }
            return result;
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
