# AR Stencil Portal — Shader Graph / HLSL Setup Guide

How to build a doorway that shows a planet / space scene only through its opening, and becomes a
fully immersive "100% VR" space once the AR camera steps through. Targets **Unity 6000.6 / URP
17.6** (this project's pipeline) and AR passthrough on Android (ARCore) / iOS (ARKit).

Pieces shipped with this guide (all under `Assets/_Project/`):

| File | What |
|---|---|
| `Shaders/PortalStencilMask.shader` | HLSL: the invisible stencil-writer surface (`CardboardXR/PortalStencilMask`) |
| `Demos/Portal/Scripts/PortalThresholdTrigger.cs` | C#: detects the head camera crossing the portal plane and toggles immersive mode |

Shader Graph is used for the interior materials; the stencil **writer** stays HLSL because Shader
Graph has no `ColorMask 0` (see §3).

---

## 1. How it works, in one picture

```
URP camera pass (opaque, sorted by material queue)

  AR passthrough background        <- camera feed, writes NO stencil, NO depth
        │
        ▼
  ① PortalStencilMask   (queue Geometry-10) ── writes stencil bit 0 = 1 in its pixels,
        │                                        no color, no depth
        ▼
  ② Interior planets + space dome (queue Geometry) ── stencil test: Equal 1
        │                                             (only pixels the mask marked render)
        ▼
  ③ Portal frame (any opaque/transparent material) ── does not touch stencil
        │
        ▼
  ④ Transparent scene objects, bloom, post
```

Before the user crosses the threshold, planets and the space dome are drawn **only inside the
portal's pixels** — everywhere else the AR passthrough shows through, because those fragments
fail `stencil == 1`. After the user steps through, the interior's second material variant drops
the stencil test entirely, so space covers the whole view.

Stencil buffers are **8 bits**. Use only bit 0 (`Ref 1` with `ReadMask 1` / `WriteMask 1`) so every
operation sees and touches just that bit; the other 7 stay free for anything else in the pipeline
(e.g. AR depth-occlusion experiments, renderer debugging) and — just as important — nothing else
can silently break your `Equal` comparison by setting a high bit in a lane the interior renders
through. Plain `Comp Equal` without masks compares all 8 bits and is fragile in exactly that way.

| Role | Stencil op | Effect |
|---|---|---|
| Mask (writer) | `Ref 1, Comp Always, Pass Replace, Read/WriteMask 1` | stencil bit = 1 inside the door |
| Interior (reader) | `Ref 1, Comp Equal, Pass Keep, Read/WriteMask 1` | drawn only where the bit is 1 |
| Interior immersive (reader) | `Ref 1, Comp Always, Pass Keep, Read/WriteMask 1` | drawn everywhere |

`Pass Keep` on readers — the act of rendering must not change the buffer. Only the writer may
`Replace`.

---

## 2. Files and materials checklist

```
M_Portal_Mask        ← PortalStencilMask (HLSL) on the portal quad          [must be created]
M_Planet_Stencil     ← interior shader, variant A: Comp Equal              [Shader Graph]
M_Planet_Immersive   ← same graph, variant B: Comp Always                  [duplicate]
M_Space_Stencil      ← dome shader, variant A                              [Shader Graph]
M_Space_Immersive    ← dome shader, variant B                              [duplicate]
M_Portal_Frame       ← any normal material (glow/emissive unlit works)      [existing]
```

Interior objects must be **opaque** (or alpha-clipped); the recipe below is written for opaque.
Alpha-blended interior objects work too — keep the same stencil block, they render in the
transparent queue as usual and still only survive where the mask marked pixels.

---

## 3. Portal mask — the writer (HLSL)

`Assets/_Project/Shaders/PortalStencilMask.shader` (shown in full — it's the sibling of
`DepthMask.shader`, with stencil replacing depth):

```hlsl
Shader "CardboardXR/PortalStencilMask"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry-10" }

        Pass
        {
            Name "PortalStencilMask"
            Tags { "LightMode" = "UniversalForward" }

            ColorMask 0          // writes no color: the passthrough/doorway shows through
            ZWrite Off           // nothing is hidden by depth; only the stencil is marked
            ZTest LEqual         // something closer to the camera than the door occludes the aperture
            Cull Off             // the door writes from either side

            Stencil
            {
                Ref 1
                ReadMask 1
                WriteMask 1
                Comp Always
                Pass Replace
            }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };

            float4 Vert(Attributes input) : SV_POSITION
            {
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half4 Frag() : SV_Target { return 0; }   // never seen; ColorMask is 0
            ENDHLSL
        }
    }
}
```

Why not Shader Graph for this pass: the mask **must write no color**, and Shader Graph gives you
no `ColorMask` toggle. Faking it (output alpha 0 in a transparent pass) moves the writer into the
transparent queue — *after* the interior — and the effect breaks. So this one pass is handwritten.
It is 20 lines and never changes.

Place it on a **quad exactly the size of the doorway opening**, parented to the portal prefab.
It renders before all opaque geometry (`Geometry-10`, same slot the project's `DepthMask` uses),
after the passthrough background.

---

## 4. Interior shaders — planets and space dome (Shader Graph)

### 4.1 Planets

1. **Create**: `Assets → Create → Shader Graph → URP → Lit Shader Graph` (or *Unlit* — stencil
   setup is identical). Name it e.g. `SG_PortalInteriorLit`.
2. In the **Graph Inspector → Graph Settings**:
   - **Surface Type**: Opaque
   - **Render Face**: Front (spheres are closed, front faces are what you see)
   - **Depth Write**: On, **Depth Test**: LEqual
   - **Stencil** (Render State group — if the group isn't visible, your target isn't Universal;
     re-pick the Universal target in Graph Settings):

     | Field | Variant A (stencil) | Variant B (immersive) |
     |---|---|---|
     | Ref | 1 | 1 |
     | Comp | **Equal** | **Always** |
     | Pass | Keep | Keep |
     | Fail / ZFail | Keep / Keep | Keep / Keep |
     | ReadMask | 1 | 1 |
     | WriteMask | 1 | 1 |

3. Author the planet surface as usual (base color, normal map, emissive for lava cracks / city
   lights, etc.). Create the material `M_Planet_Stencil`.
4. **Variant B**: duplicate the graph (`SG_PortalInteriorLit_Immersive`), flip **Comp to Always**
   only, keep everything else. Material `M_Planet_Immersive`. Keep the two ports in sync when you
   edit the graph — a Sub Graph for the shared body of the shader avoids double edits.

The stencil block cannot vary **per material** — it is baked into the shader, not a property.
That's why variant B exists rather than a boolean.

### 4.2 Space dome (the skybox problem)

URP's built-in **Skybox pass ignores the stencil buffer** — a `Unity Skybox` material can never be
confined to the portal. The stencil-correct stand-in is an **inside-out sphere** using the same
interior shader:

1. Create a Sphere, radius ≈ 50–100 m, centered on the portal world (or parented to the portal
   root so both move together), `Static` optional.
2. Use `SG_PortalInteriorUnlit` (an Unlit graph with the same stencil block, Render Face: **Back**)
   — camera sits inside the sphere, the fragments it sees are back faces.
3. Texture: a cube-projected space skybox (texture sampled by direction) or a plain unlit night
   color with emissive stars as a textured unlit surface. Either works; the stencil is what makes
   it a portal world.
4. Materials `M_Space_Stencil` / `M_Space_Immersive` (same Comp Equal / Always pair).

### 4.3 Portal frame

The frame/bezel (posts, lintel, emissive ring — see `PortalWorld.BuildFrame` for this project's
frame construction) uses any ordinary material. It must **not** write the stencil; draw it in the
opaque or transparent queue as it already is. Depth testing keeps it glued in front of whatever
the portal shows (it's at the door's plane; planets are behind it), so draw order vs. the interior
doesn't matter in practice.

---

## 5. Threshold detection — when the camera steps through

`Assets/_Project/Demos/Portal/Scripts/PortalThresholdTrigger.cs` is in the repo. It treats the
portal's local XY plane (normal = portal's `forward`) as the threshold and tracks the **main AR
camera's signed distance** to it in `LateUpdate` — cheap, deterministic, no physics rig needed:

```csharp
var inside = SignedDistance() <= 0f ^ m_FlipDirection;
```

On every crossing it:

1. **Swaps** every interior renderer registered in `m_InteriorRenderers` between
   `m_StencilMaterial` → `m_ImmersiveMaterial` (and back), slot by slot, via `sharedMaterials`
   (materials are *not* cloned);
2. **Disables/enables** the stencil mask renderer (`m_StencilMask`), so nothing writes `1` anymore;
3. **Fires** `m_ImmersiveChanged` (inspector `UnityEvent<bool>`) and the `ImmersiveChanged` C#
   event — your hook for the AR-specific part.

Threshold plane or trigger collider? A `BoxCollider` + `OnTriggerEnter` works, but ties the head's
collision to the physics matrix (head is usually driven by XR tracking, outside the physics scene),
needs a physics tick, and double-fires at the boundary. The plane distance is exact and cannot
flicker; it also fires for teleports through the door. If you must use a volume, follow the same
swap payload from a `OnTriggerEnter/Exit` pair — the `Apply` method is all you need.

### Wiring it up

1. Add **Portal Threshold Trigger** to any object (e.g. the portal root).
2. `m_Portal` — the portal quad transform. `m_Camera` — leave empty to use `Camera.main`.
3. `m_InteriorRenderers` — drag in the space dome and every planet renderer.
4. `m_StencilMaterial` = `M_Planet_Stencil` (or `M_Space_Stencil` — one swap covers all slots that
   hold it; use a list-of-pairs variant or groups if your interiors intentionally mix two stencil
   materials with two immersive peers).
5. `m_ImmersiveMaterial` = `M_Planet_Immersive`.
6. `m_StencilMask` — the quad carrying `PortalStencilMask`.
7. `m_FlipDirection` — enable if your portal's `forward` points away from the viewer.

### "100% VR" / passthrough off

With variant B in place, space covers every pixel of the view — the immersive swap alone already
*looks* like VR. To also stop drawing the AR camera feed (saves the background pass and avoids a
flat feed under semi-transparent space effects), hook your AR-specific toggle to
`m_ImmersiveChanged(true/false)` in the inspector. Typical options with this project's stack:

- **Android (ARCore) / iOS (ARKit) on URP** — the passthrough is drawn by the **AR Background
  Renderer Feature** on the Universal Renderer Data asset. Switch to a Renderer asset *without*
  that feature for immersive mode (assign at runtime through your camera/renderer-switching path,
  or split scene/demo content per build), or simply leave it on — visually you still get full
  coverage from the space dome.
- **Solid fallback** — set the camera's solid clear color to opaque black for immersive mode; the
  space dome then composites over black instead of over the room.
- Do **not** disable the AR session to kill the feed — you keep head tracking and planes, only
  lose the image.

`OnDisable`/leaving through the door reverses everything: stencil variants back, mask re-enabled,
hook fires with `false`.

---

## 6. Rendering order reference

All interior materials keep Unity's default opaque queue (`Geometry`, 2000); no custom queues are
needed beyond the mask's `Geometry-10`.

| Queue | Pass | Color | Depth | Stencil |
|---|---|---|---|---|
| Background (1000) | AR passthrough | writes | — | untouched |
| Geometry-10 (1990) | PortalStencilMask | none | none (test only) | `bit0 := 1` everywhere under the door |
| Geometry (2000) | Planets (stencil) | writes | LEqual, writes | Equal 1 required |
| Geometry (2000) | Space dome (stencil) | writes | LEqual, writes | Equal 1 required |
| Geometry-2000 | Portal frame | writes | LEqual, writes | none |
| Transparent (3000) | interior transparent parts, AR UI | blends | — | Equal 1 if interior |

Immersive mode removes nothing from the table — variant B materials simply pass `Comp Always`,
and the mask is disabled.

---

## 7. Verification

1. **Frame Debugger** (`Window → Analysis → Frame Debugger`): capture a frame looking at the portal
   from the room side. Find the mask draw (`PortalStencilMask/UniversalForward`), then confirm the
   planet draws appear **only inside its screen rectangle** and show `stencil equal` in their
   state. A planet drawn outside the door rectangle means a variant with `Comp Always` is in place,
   or the interior renderers list in the trigger is swapped.
2. **RenderDoc**: attach, capture, and inspect the stencil texture after the mask pass — the door
   should read as a white (1) shape with black (0) elsewhere.
3. **In headset**: walk through slowly. At the plane crossing you should get the frame-level solid
   switch to space with zero pop of remaining passthrough pixels; walk back and the room returns.
4. **Scene view**: the effect works there too (the scene camera is another opaque pass) — handy
   for checking the mask's size matches the door exactly.

## 8. Failure modes and fixes

| Symptom | Cause | Fix |
|---|---|---|
| Planets visible everywhere | interior material is the `Immersive` (Always) variant; or Frame Debugger shows stencil state missing | swap M_Planet_Stencil in; re-check variant A `Comp` |
| Nothing renders inside the door, scene looks "clean" | mask drawn *after* interior (queue too high), or mask quad missing/disabled | mask must be `Geometry-10`; check renderer enabled, `ZTest LEqual` |
| Door shows black rectangle | interior shaders fail stencil (`Pass Replace` typo; `ReadMask` mismatch) | writer/reader masks must both be `1` |
| Swapping in the trigger does nothing | `m_InteriorRenderers` empty, or materials aren't the two variants registered | drag renderers; use both M_*_Stencil/Immersive pair |
| Interior pops out one eye / edge artifacts | one planet still has the stencil material while others went immersive | immersive swap is per-renderer — check the list |
| Bloom bleeds from the portal onto the room | URP Bloom is screen-space; it does not know the aperture | keep portal HDR emission below the bloom threshold, or accept the halo (it reads as glow spilling from the doorway, which is usually wanted) |
| Planet cut by door edge looks hollow from inside the doorway | interior sphere normals: Render Face on dome variant | dome must be `Render Face: Back` (or normals inverted) |
| Door edge shows a passthrough fringe | mask quad slightly smaller than the opening | scale the mask to the aperture exactly; the frame, never the mask, should overlap the seam |
| Works in Editor, not on device | AR depth occlusion or MSAAx4 differences | both platforms support stencil; verify depth+stencil format (D24S8) on the device's URP asset |

## 9. Notes and limits

- **One bit used, seven free.** Keep `Ref 1 + masks 1` for this portal; a second portal uses `Ref 2`
  (bit 1) with its own mask and `Ref 2 / ReadMask 2 / WriteMask 2` interior variants.
- **Trigger depth**: the threshold is infinite in plane dimensions; the swap fires on the door's
  infinite plane, same as `PortalWorld.Crossing` in a strip rather than a volume. If you need the
  door's physical width/height to gate the crossing, use `PortalWorld.Crossing(before, after, …)`'s
  rectangle test and call `Apply` from it — it's the rectangle+margin logic already in the demo.
- **Existing demo**: `Demos/Portal` opens its portal with `CardboardXR/DepthMask` (hide virtual
  pixels via depth; see that shader's header). The stencil system here is the more general tool:
  it confines by screen region instead of by depth, which is why the "100% VR" swap can work with
  zero scene surgery. The two compose — nothing stops a stencil portal from also using depth masks
  for its door cover during the rise animation.
- **Per-instance variation**: tint planets via `MaterialPropertyBlock` as `PortalWorld` does —
  stencil lives in the shader, blocks only touch material uniforms.
- **Culling**: keep the mask quad on the same portal root as the door geometry and it culls
  correctly with it; no special flags needed at door scale.

## 10. Acceptance checklist

- [ ] `PortalStencilMask` material on the door quad only; quad exactly door-sized
- [ ] Planets + space dome use the `Comp Equal` variants from the room side
- [ ] Frame Debugger shows interior draws confined to the door's rectangle
- [ ] `PortalThresholdTrigger` attached; crossing swaps materials and hides the mask
- [ ] `m_ImmersiveChanged` wired for passthrough/AR-background if you want it fully off
- [ ] Walking back through restores the room-masked view exactly
- [ ] No bloom from inside the portal reads as light leaking into the room (or it's tuned out)
