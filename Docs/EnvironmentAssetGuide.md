# Environment Asset Guide

How to replace the primitive environment kit with real art. The generator never changes: a theme asset
(`EnvironmentTheme`) maps each semantic element to one or more prefabs, and the builder places them under a `Visuals` node.
The prototype kit is made by `Blackglass > Environment > Create Prototype Kit` (`EnvironmentKitBuilder`); its prefabs are the
reference for every number below.

## 1. Units and grid
1 Unity unit = 1 m. The mission grid is 1 m tiles. Y is up. In Blender export FBX with **Apply Scale** and
**-Z Forward, Y Up**, so the Unity import has scale 1 and no rotation.

## 2. Modules
Footprints are X x Y x Z metres. The front of every module is +Z. Wall-class modules are 3 m tall and stay inside their
1 x 1 tile.

| Element | Footprint | Pivot | Authored orientation |
|---|---|---|---|
| Floor | 1 x 0.2 x 1 | centre of top face (top at y = 0) | flat |
| WallStraight | 1 x 3 x 1 | bottom-centre | runs along X; the +Z and -Z faces are the two room sides |
| WallEnd | 1 x 3 x 1 | bottom-centre | connects toward +Z; free faces are -Z and the sides |
| WallCorner | 1 x 3 x 1 | bottom-centre | connects +Z and +X (free corner at -X, -Z) |
| WallJunction | 1 x 3 x 1 | bottom-centre | connects +X, -X and +Z; free face is -Z |
| DoorFrame | 1 x 2.4 x 1 | bottom-centre | overlay on a WallEnd: same footprint and orientation (jambs and header on the -Z side) |
| Pillar | 1 x 3 x 1 | bottom-centre | symmetric |
| LowCover | 1 x 1 x 1 | bottom-centre | long face along X |
| LowCoverLong | 2 x 1 x 1 | bottom-centre, centred on its own footprint | runs along X |
| Crate / Cabinet | up to 1 x 1 x 1 | base-centre | front +Z; 1 m tall |
| Terminal | 0.8 x 1.2 x 0.8 | base-centre | front +Z; screens are objects named `Display` |
| LightFixture | thin prop at about y = 2.45 | tile frame | on the +Z face of a wall tile, within about 0.1 m of it; emissive only, no real light |

In the built kit, detail on wall-class modules (conduits, free-face plates) sits at z = +-0.5, so it stays inside the
footprint; thin strips and bands stick out 0.01-0.02 m. Nothing goes further than 0.1 m outside a tile.

## 3. Forward and yaw
Front = +Z. The generator rotates modules in 90 degree steps, clockwise seen from above. Never rely on a rotation in code:
fix orientation inside the prefab by putting the mesh under a child transform and rotating that child.

## 4. Naming
Prefabs `<Element>[_Variant]` (`WallStraight`, `LowCover_B`); meshes `SM_<Element>_<Variant>`; materials `BW_<Name>`.

## 5. Materials
Reuse the six `BW_` materials where possible (Concrete, Metal, Floor, Prop, Accent, Display). URP Lit, one material per
renderer where possible. Never create materials at runtime. Emission is set through `_EmissionColor` only.

## 6. Colliders
Prefabs have **none**. The generated gameplay object under `Geometry` carries the single BoxCollider, `CoverSurface` and
`NavMeshModifier`; its renderer is disabled when a theme is assigned. Any collider left on a prefab is removed at build
time. Decorative detail may protrude at most 0.1 m outside the tile footprint and never changes cover or navigation.

## 7. From mesh to module
1. Model or generate the piece (any tool).
2. Clean it in Blender: apply all transforms, set the pivot per the table, scale 1, front +Z.
3. Export FBX (settings in section 1).
4. Import into `Assets/_Project/Environment/` (or a theme folder): scale 1, no mesh colliders.
5. Make a prefab with the mesh under a child transform (rotate or offset that child to correct orientation).
6. Open `Assets/_Project/Environment/Themes/<Theme>/<Theme>.asset` and put the prefab in the element's `variants` list:
   append for a new variant, replace to swap.
7. Press Play in `ProceduralMission`. F8 shows the gameplay cubes (with their placeholder materials) so you can compare the
   art against the real collision volumes. The terminal stays visible in that view.

## 8. Variants
More than one prefab in a list gives deterministic variation by seed and tile (a stateless hash with the theme `salt`).
A variant never changes gameplay, so keep variants to the same footprint and height.

## 9. New theme
Duplicate the theme asset, change `salt` and the prefabs, and assign it to the `MissionDirector.environmentTheme` field.

## 10. Checklist before import
- Scale 1 (1 unit = 1 m), no leftover object scale.
- Pivot as in the table (bottom-centre, floor top-centre, and so on).
- Front faces +Z after export.
- No colliders on the mesh or prefab.
- Shared `BW_` materials, not new ones per prop.
- Wall-class modules (walls, corner, junction, pillar) are 3 m tall within a 1 x 1 footprint.
