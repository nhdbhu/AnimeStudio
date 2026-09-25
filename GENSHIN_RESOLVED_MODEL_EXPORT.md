# Genshin resolved model export

This branch can optionally build a strict, model-scoped `.resolved` package beside the normal FBX export.
Normal AnimeStudio model export remains unchanged unless the resolved-package toggle is enabled.

## Correct load/export order

1. Select the Genshin game profile.
2. Keep **Resolve dependencies** enabled.
3. Load the Genshin **CAB map** before loading the source BLK(s)/corpus.
4. Load the source BLK(s) or source corpus. AnimeStudio's existing CAB-map dependency loader remains the authority for external CAB resolution.
5. Use **Export -> Load DummyDll folder for resolved model export** and select the Genshin DummyDll directory.
6. Use **Export -> Check resolved model export readiness**. Resolve dependencies, CAB map, and DummyDll should all show ready.
7. Enable **Export -> Build strict .resolved package with model exports**.
8. Select the exact GameObject/model root and export the model normally.

Do not identify same-name roots by name alone. The package records source CAB/original path/PathID provenance for the selected root.

## Output

For an FBX named `Model.fbx`, AnimeStudio still writes the normal FBX and normal sidecar outputs, and additionally writes:

```text
Model.resolved/
  Model/
  Hierarchy/
  Meshes/
  Materials/
  Textures/
  Cubemaps/
  Shaders/
    Programs/
    Passes/
  Components/
  Dependencies/
  Provenance/
  manifest.json
  VALIDATION_OK.txt        # only when strict validation passed
  # or VALIDATION_FAILED.txt
```

The package uses readable Unity/game names where available. CAB/path/hash identity is retained in metadata rather than being used as the canonical filename.

## What the resolved package closes

- selected GameObject hierarchy and parsed components
- MeshFilter / SkinnedMeshRenderer / renderer references
- every Renderer material slot in authoritative order, including slots not represented by FBX polygon material indices
- Material -> Shader
- every non-null Material texture binding
- Texture2D exact payload plus converted PNG when conversion succeeds
- Texture2D sampler, color-space, mip, platform and stream metadata
- Cubemap identity, sampler metadata and exact inline/external streamed payload
- Cubemap source-face Texture2D PPtrs when serialized by Genshin
- Shader dependencies and non-modifiable texture bindings
- exact extracted GPU program bytes plus pass/stage/blob/bind-channel metadata
- MonoBehaviour decoded data through DummyDll plus PPtrs discovered in the decoded object
- ParticleSystemRenderer base Renderer/material closure; type-tree PPtrs are additionally traversed when the Unity type tree is available
- raw object provenance for verification where applicable

Animator/controller libraries are deliberately not followed without bounds. The exporter is model-scoped and has object/edge safety limits to avoid accidentally traversing an unrelated global animation library.

## Validation behavior

The resolved exporter is intentionally fail-closed for rendering-critical gaps. A package is not marked valid when, for example:

- dependency resolution is disabled
- no CAB map is loaded
- a non-null model-scoped PPtr cannot be resolved
- a Renderer material dependency cannot be resolved
- a referenced Texture2D/Cubemap payload cannot be read
- a referenced Shader is only available as an unparsed raw Shader
- a Shader has serialized GPU program data but exact programs cannot be extracted
- a MonoBehaviour encountered by the selected hierarchy cannot be decoded because DummyDll is missing/incomplete
- a renderer exists only as an unparsed raw object, so authoritative material closure cannot be proven

Warnings may still be present in a valid package for non-critical provenance-only objects or optional conversions.

`VALIDATION_OK.txt` means the implemented strict invariants passed for that export. It is not permission to ignore the manifest; `manifest.json` remains the detailed provenance and dependency record.
