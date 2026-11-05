# Vision Pro-Style Hand Tracking on Unity 6 — Implementation Guide

How this project's “Vision Pro-style” hand interactions work, what “native Apple Vision Pro”
actually takes, and which parts are in the repo today. Targets **Unity 6000.6 / XR Hands 1.9.0 /
XRI 3.6.0** (this project's versions). Verified in this repo: everything below compiles; API
signatures were checked against the installed packages (XR Hands 1.9.0, XRI 3.6.0, including its
built-in `visionOS` sample). What **cannot** be verified without a Vision Pro: anything marked
“visionOS only”.

## 0. Reality check first — three different targets

| | This app today (iPhone AR) | visionOS app (mixed reality) | visionOS app (fully immersive) |
|---|---|---|---|
| What it is | AR passthrough on iOS/Android; hand tracking from **BlazeHand ONNX on the camera image**, fed into XR Hands via `HandSubsystemFeed` | PolySpatial bounded/unbounded volume; content rendered by RealityKit; Unity builds via XRI | PolySpatial Metal / “fully immersive”; Unity renders directly |
| Hand data available to your code | **Full skeletal** (26 joints), via the MediaPipe feed | **No skeleton.** Discrete `<SpatialPointerDevice>` events only (gaze + pinch/poke) | **Full skeletal** via ARKit where the platform exposes it |
| Build requirements | Unity 6 + iOS module (or Android) — nothing new | macOS, Unity **Pro** license, visionOS module, `com.unity.polyspatial.xr`, `com.unity.polyspatial.visionos`, Xcode with visionOS SDK, a **Vision Pro to run it on** | Same, minus RealityKit constraints |
| Where it compiles | This machine ✓ | Not here (Linux, no visionOS toolchain) — needs your Mac | Not here |

The key fact, straight from XRI's visionOS sample docs (in this repo's package cache): *“volumes do
not have access to the hand skeleton data”* and the `SpatialPointerInput` actions are *“configured
to handle poke, direct, and indirect pinch”* via `<SpatialPointerDevice>/spatialPointer0|1`. A
“Vision Pro-style” **feel** — pinch, drag, scale, direct touch — is achievable in the MR volume
mode with spatial pointers; a **continuous hand mesh glove** is not, except where the platform
gives you skeletal poses (this app's iPhone build today, or a fully-immersive visionOS app).

Requirements 1–4 therefore map onto two tracks:

- **Track A — works in this repo today** (iPhone/Android build, MediaPipe→XR Hands feed):
  skeletal glove visualizer, pinch with hysteresis, two-hand zoom/rotate, one-hand drag, fingertip
  touch. This is what the solar-system demo uses.
- **Track B — visionOS-native**: spatial pointer interactions through XRI's visionOS sample
  (`XRI_SimpleRig`, `NearFarInteractor`, `XRPokeInteractor`, `XRGrabInteractable` with **Select
  Mode = Multiple** and **GeneralGrabTransformer → Allow Two Handed Scaling**, Movement Type
  **Velocity Tracked** to throw). Needs your Mac, a Pro license, PolySpatial packages and a Vision
  Pro — see §6.

If the goal is “ship on AVP”, the honest plan is: keep Track A interactions as the gameplay layer
(they're platform-agnostic), port the Rig/materials to PolySpatial on the Mac, and use Spatial
Pointers where PolySpatial gives you nothing but events.

## 1. Requirement 1 — Skeletal tracking & glove/mesh visualizer

### What the data looks like

Unity XR Hands normalizes every platform's hand skeleton onto one enum, `XRHandJointID`:
**26 valid joints per hand** — `Wrist`, `Palm`, and 24 finger joints (thumb:
Metacarpal→Proximal→Distal→Tip; fingers: Metacarpal→Proximal→Intermediate→Distal→Tip). ARKit's
native skeleton (more granular in Apple's docs) is **mapped onto this** — code against
`XRHandJointID`, never against raw ARKit indices. `Handedness` comes from
`XRHand.handedness`/`subsystem.leftHand`/`rightHand`; `hand.isTracked` gates validity.

This app supplies that data on-device from BlazeHand (see `CardboardXR.Hands.HandSubsystemFeed`,
which runs the provider and pushes poses **in the XR Origin's camera floor offset space** — hence
every reader here converts through a `m_TrackingSpace`). It updates on every camera image by
default (the landmarker is the CPU's largest recurring cost), or at the rate pinned by the `handHz`
launch option.

### The visualizer — `Assets/_Project/Scripts/Hands/HandGloveView.cs`

Two modes, one component (also in this repo):

| Mode | What it does | Touch it by |
|---|---|---|
| `Primitives` (default) | Capsules (bones) + spheres (joints), colored by finger, from this repo's `m_Material` or an auto-created URP Unlit | Drop it in, works immediately |
| `Rig` | Poses **your glove's** hierarchy: every descendant named like a joint (`Wrist`, `Palm`, `IndexTip`, …, optional `XR_` prefix) gets the subsystem's world pose, positions **and rotations** | Assign `m_LeftRig` / `m_RightRig` |

Setup for a real glove mesh:

1. Skin your glove with a skeleton that mirrors the XR Hands hierarchy: `Wrist → Palm` and one
   chain per finger (metacarpal base at the palm), names exactly the `XRHandJointID` names.
2. Assign the rig roots to `m_LeftRig`/`m_RightRig`. The mapping is by name, not by hierarchy,
   so extra helper bones (e.g. `Corrective_Little`, `IK_Hint_Index`) can coexist.
3. Keep `m_TrackingSpace` unset to auto-find the XR Origin's camera floor offset (Track A), or
   assign it explicitly (Track B, Metal immersive).
4. Joints ease (`m_EaseSeconds`, exponential damp) with a 0.25 m snap threshold; hands stop being
   drawn when `isTracked` goes false; the glove hides in passthrough.

A skinned mesh deforms correctly from this because each bone receives its **world pose from the
subsystem** and Unity computes the local rotations — no IK, no bone-length fitting. Any deformity
you see is the provider's data, not the mapping.

### What Track B adds — nothing here exists there

On a PolySpatial **volume** there is no `XRHandSubsystem` to read: no glove, no pinch distance, no
poke angle. Interaction is `<SpatialPointerDevice>` input into XRI. On a **fully immersive**
visionOS app the platform provides skeletal data and this same `HandGloveView` works unchanged —
with an appropriate tracking space and whatever `XRHandSubsystem` PolySpatial exposes on that
path (verify against the PolySpatial release notes you ship against; do not assume a Unity editor
to validate it).

## 2. Requirement 2 — Vision Pro gestures (pinch, grab, drag, throw)

### Pinch detection

| Path | How "pinch" is decided | Threshold | Files |
|---|---|---|---|
| Track A (skeletal) | thumb/index tip distance with hysteresis (Apple-style 2 cm-class feel) | `m_PinchDistance` 0.025 m for metric hands (this project runs 0.035 m for MediaPipe) | `SolarSystemTwoHandController` (per-hand `PinchGate`), `HandSubsystemFeed` |
| Track B (MR) | ARKit itself declares the pinch; discrete `Begin → Perform → End` events | none — there is no distance to threshold | `<SpatialPointerDevice>/...` → `SpatialTouchInputReader` (XRI sample) |

Requirement 2's “< 2.5 cm threshold” is satisfied on Track A by `SolarSystemTwoHandController.m_PinchDistance = 0.025f`. On Track B there is no threshold to write — that's the platform's gesture detector.

### Pinch-and-drag / grab / throw (Track A — in this repo)

- One hand pinched: the object is carried at the thumb–index midpoint with a grab offset (no pop; re-anchors when the *other* hand takes over or when one of two releases).
- Planet-or-system: grabbing within `m_GrabRadius` of a planet grabs/selects that planet (then animates as a frame of the solar system); otherwise the whole system.
- Throw: the direct-carry path does not throw (it's direct manipulation). For thrown planets,
  make the planet an `XRGrabInteractable` with **Movement Type = Velocity Tracked** — XRI throws
  on detach (this project's `CubePhysics` uses exactly that; cap with `k_MaxThrowSpeed`).
- The solar-system controller additionally does things XRI can't out of the box: chord-delta
  two-hand zoom (0.25×–4×), chord-frame two-hand 3D rotation (pitch/yaw/roll from the line
  between the pinches).

### Pinch-and-drag / grab / throw (Track B — visionOS MR)

Add the XRI **visionOS** sample to your project on the Mac (`Package Manager → XR Interaction Toolkit → Samples → visionOS`), then:

1. Drop **XRI_SimpleRig** into your scene (it wires the `Primary/Secondary Interaction Groups` with poke, direct pinch, indirect pinch input readers).
2. On every grabbable: `XRGrabInteractable`, **Select Mode = Multiple** (two hands can select), and velocity-tracked or instantaneous movement as suits the object; add `GeneralGrabTransformer` with **Allow Two Handed Scaling** for two-hand scale.
3. Gaze + pinch to select at distance (`XRRayInteractor` with the sample's `VisionOSFarCaster` pattern), direct pinch near touch (`PointNearCaster` gives near-hand overlap priority), poke for buttons (`XRPokeInteractor` + `PokeInteractorToggler`, because poke is ill-suited to Vision Pro's discrete input).
4. No pinch distance anywhere — ARKit and the `SpatialTouchInputReader` do press/release.

### Two-hand rotation

- Track A: `SolarSystemTwoHandController` — an orthonormal **chord frame** (x = left→right pinch vector, z = x × world-up); each frame's `frameNow * Inverse(frameAtGrab)` rotates the object; full pitch/yaw/roll, Slerp-eased, with a 60°/frame glitch guard.
- Track B: `GeneralGrabTransformer.AllowTwoHandedScaling` handles scale; rotation follows hand orientation for grabbable objects (grab transforms rotate with both hands).

## 3. Requirement 3 — Direct fingertip touch & proximity

### The component — `Assets/_Project/Scripts/Hands/FingertipTouch.cs`

Concrete, this repo, compile-verified:

- **Zone**: any target's surface — exact for sphere colliders; `bounds.ClosestPoint` for other colliders; renderer bounds when there's no collider at all.
- **Two stages**: hover at `m_HoverDistance` (8 cm — soft tint), touch at `m_TouchDistance` **3 cm** (full glow + events). URP `_BaseColor` tint + `_EmissionColor` glow (materials are instances; `_EMISSION` enabled for Lit), exponential-damp fade.
- **Events**: `TouchChanged(Transform, bool)`/`HoverChanged(Transform, bool)` (C#) plus a serialized `m_TouchChanged(UnityEvent<Transform,bool>)` for the inspector — wire your haptic/sound/selection hook there. `IsTouching`/`Touched` for game logic.
- Works with any `XRHandSubsystem` (Track A and a visionOS Metal build); only needs the index `IndexTip` pose.

### Haptics — what Apple actually gives you

- Vision Pro: **no hand haptics** — there is no motor for fingers. The idiom is a subtle sound plus the visual state change; wire `m_TouchChanged` to an `AudioSource` tick.
- iPhone: CoreHaptics via a small native plugin (`UIImpactFeedbackGenerator` "light" on touch begin is a 10-line `.mm`); or reuse the project's existing `DeviceState.mm` pattern.

A solar-system-specific touch (planets only) already exists inside `SolarSystemTwoHandController` — `FingertipTouch` is the standalone, reusable version of the same math for any target set (planets, buttons, the portal frame).

## 4. Requirement 4 — Performance & thermal on visionOS

Vision Pro's big advantage is hardware, not app discipline: hand tracking runs **out-of-process** on Apple silicon (ARKit on the NPU), so there is nothing like this repo's BlazeHand CPU cost to throttle. Budget accordingly:

1. **Frame budget**: the system runs 90 Hz; consistency beats peak. Target 90 always or pin 60 deliberately — frame pacing errors are worse than a lower rate. On this app (iPhone) the shipped config is 60 Hz + `DynamicResolutionScaler`; keep that story separate from visionOS builds.
2. **PolySpatial MR**: content shares RealityKit's GPU; keep your scene small — texture ≤ 2K (ASTC), few transparent layers, Shader Graph mandatory for custom materials, no realtime shadows unless you opt in. Measure in Xcode Instruments (GPU frame capture + Time Profiler), not the Unity Profiler alone.
3. **Immersive Metal**: you own Metal; watch draw-call count, use foveation where available, MSAA 2–4× (bounded MR volumes don't give you MSAA control — it's their choice).
4. **Code**: no per-frame allocations in `updatedHands` or `LateUpdate` (cache `XRHand` poses in arrays; no `GetComponent`/`Find` in hot paths — see this repo's `HandSubsystemLocator`); no string building per frame; no `Material` instances per touch (this code uses instanced materials + `MaterialPropertyBlock`-style batching where it matters); pose smoothing is ~cheap.
5. **Update cadence**: don't poll joints you don't use; this repo reads index tips and pinch points only.
6. **Thermal (this phone-class hardware)**: the pipeline from the previous perf pass applies as-is — `HandTrackingThrottler` (60→30→24 Hz on iOS/Android), `DynamicResolutionScaler` (eye textures 0.8–1.0 by GPU frametime), `SurfaceScanCleanup` (stop ARCore scanning after initial surfaces). On visionOS these don't apply; on Metal immersive you still want lower update rates for non-critical visuals.
7. **Never infer hand data by screen-space heuristics** on visionOS — use the subsystem; ARKit does the work for you out-of-process.
8. **Battery**: every hand frame still costs RealityKit sync in MR mode; don't request unused XR features (eye tracking, world anchors) in PolySpatial project settings.

## 5. Requirement → status in this repo

| # | Requirement | Status | Files |
|---|---|---|---|
| 1 | Skeletal tracking + glove/mesh visualizer | ✅ works today (Track A); ⚠️ visionOS MR carries no skeleton by design; ✅ Metal immersive feeds the same code | `HandGloveView.cs`, `HandSubsystemLocator.cs`, `HandJointsView.cs` (21-dot debug view), `HandSubsystemFeed.cs` |
| 2 | Pinch / drag / throw / two-hand scale & rotate | ✅ Track A complete (2.5 cm = set `m_PinchDistance`); ✅ Track B via XRI visionOS sample (Multiple select + GeneralGrabTransformer) | `SolarSystemTwoHandController.cs`, `CubePhysics.cs`, `HandInteractorRig.cs`, XRI visionOS sample |
| 3 | Fingertip touch & proximity | ✅ `FingertipTouch` (hover 8 cm → touch 3 cm, glow + events, UnityEvent haptic hook) | `FingertipTouch.cs` |
| 4 | Performance & thermal guidance | ✅ §4 + the previous perf pass (`HandTrackingThrottler`, `DynamicResolutionScaler`, `SurfaceScanCleanup`) | `ThermalState.cs`, `HandTrackingThrottler.cs`, `DynamicResolutionScaler.cs`, `SurfaceScanCleanup.cs` |

## 6. Bringing up visionOS on your Mac (Track B checklist)

1. macOS with PolySpatial access: **Unity Pro** (required for the PolySpatial packages), Unity 6000.6.x with the **visionOS build support** module, Xcode with the visionOS SDK.
2. Install packages (Package Manager): `com.unity.polyspatial.xr`, `com.unity.polyspatial.visionos`, Shader Graph, AR Foundation — pin exact versions from your PolySpatial release notes, not "latest".
3. `Edit > Project Settings > XR Plug-in Management > visionOS`: enable the PolySpatial plug-ins if prompted; fix validation issues.
4. Import **XR Interaction Toolkit → visionOS** sample; use `XRI_SimpleRig` as your rig; confirm `VolumeCameraWindowConfiguration` is assigned to the volume camera (it's in the sample's `Resources/`).
5. Scene: keep your solar system; enable **Select Mode = Multiple** on `XRGrabInteractable`s; `GeneralGrabTransformer` → Allow Two Handed Scaling; velocity-tracked movement for things that should fly.
6. Shaders: Shader Graph only (PolySpatial constraint) — port any opaque custom HLSL (e.g. the portal mask from `PORTAL_SETUP.md`) before shipping to a volume.
7. Build → run on device (no simulator hand tracking); test two hands, one hand + gaze, and the door/portal carry-through if you adopt it.
8. Nothing in §6 was executable in this Linux container — validate each step on your Mac before committing; APIs in this document were checked against XRI 3.6.0 and XR Hands 1.9.0 *as installed in this repo*.
