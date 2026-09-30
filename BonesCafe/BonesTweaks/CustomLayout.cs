using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Tilemaps;

namespace BonesTweaks
{
    // Builds a restaurant of any width x depth from the largest layout (AreaLayout5, 25 wide x 15
    // deep). Every layout is a self-contained object: a Grid of Tilemaps (floor, walls, the
    // walkable front wall, the street outside), four invisible boundary colliders, black
    // "screen blockers" hiding the outside, 2D lights, customer spawn areas, a camera and a
    // NavMeshPlus surface. The front wall with the door is at the top and never moves; the room
    // grows or shrinks to the left, right and bottom.
    //
    // Everything is stretched with one rule, applied to tiles and objects alike:
    //   - left of the door        -> moves with the left wall
    //   - right of the door       -> moves with the right wall
    //   - the lower part          -> moves with the bottom wall
    //   - the middle of the room  -> kept, and repeated (walls have a 4-tile pattern) to fill gaps
    // then the camera is refitted and the walkable area rebuilt.
    internal static class CustomLayout
    {
        // Layout 5, in tile coordinates (the Grid is offset by half a tile, so a tile at (x, y)
        // covers world x-0.5..x+0.5).
        private const int DoorX = 3;
        private const int OrigX0 = -8, OrigX1 = 16;  // floor columns
        private const int OrigY0 = -11, TopY = 3;    // floor rows; the front wall is at y 4..5
        private const float SouthSplitY = -4f;       // world y below which things follow the bottom wall

        internal const int MinWidth = 12, MaxWidth = 60, MinDepth = 5, MaxDepth = 40;

        internal static int X0, X1, Y0;
        private static int DxW => X0 - OrigX0;
        private static int DxE => X1 - OrigX1;
        private static int DyS => Y0 - OrigY0;

        /// <summary>Floor rectangle for a room of the given size, keeping layout 5's door offset.</summary>
        internal static void SetSize(int width, int depth)
        {
            width = Mathf.Clamp(width, MinWidth, MaxWidth);
            depth = Mathf.Clamp(depth, MinDepth, MaxDepth);
            int left = Mathf.RoundToInt((width - 1) * 11f / 24f);   // layout 5: 11 left of the door, 13 right
            X0 = DoorX - left;
            X1 = X0 + width - 1;
            Y0 = TopY - depth + 1;
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;

        // Where the tile at a new position comes from in the original layout.
        private static int MapX(int x)
        {
            if (x <= X0 + 1) return x - DxW;
            if (x >= X1 - 1) return x - DxE;
            if (x >= OrigX0 + 2 && x <= OrigX1 - 2) return x;
            return -6 + Mod(x + 6, 4);   // plain stretch of the front wall's 4-tile pattern
        }

        private static int MapY(int y)
        {
            if (y >= TopY - 1) return y;
            if (y <= Y0 + 1) return y - DyS;
            if (y >= OrigY0 + 2 && y <= TopY - 2) return y;
            return -9 + Mod(y + 9, 4);
        }

        // Where a world position in the original layout ends up.
        private static float StretchX(float x) => x < DoorX ? x + DxW : x + DxE;
        private static float StretchY(float y) => y < SouthSplitY ? y + DyS : y;

        internal static void Build(GameObject layout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tiles = 0;
            foreach (var tm in layout.GetComponentsInChildren<Tilemap>(false)) tiles += Retile(tm);

            foreach (Transform child in layout.transform)
            {
                var n = child.name;
                if (n.StartsWith("Boundary")) StretchCollider(child);
                else if (n.StartsWith("ScreenBlocker")) StretchSprite(child);
            }

            var spawns = layout.transform.Find("CustomerManager/SpawnAreas");
            if (spawns != null)
                foreach (Transform s in spawns)
                    s.position = new Vector3(StretchX(s.position.x), StretchY(s.position.y), s.position.z);

            var lights = layout.transform.Find("Lights2D");
            if (lights != null)
                foreach (var light in lights.GetComponentsInChildren<UnityEngine.Rendering.Universal.Light2D>(true))
                    StretchLight(light);

            FitCamera(layout);

            var surface = layout.GetComponentInChildren<NavMeshSurface2d>(false);
            if (surface != null) surface.BuildNavMesh();
            else Plugin.Log.LogWarning("Custom cafe size: no NavMeshSurface2d found; customers may not reach new areas.");

            Plugin.Log.LogInfo($"Custom cafe size: {X1 - X0 + 1} wide x {TopY - Y0 + 1} deep " +
                               $"(floor x {X0}..{X1}, y {Y0}..{TopY}), {tiles} tiles, {sw.ElapsedMilliseconds} ms");
        }

        private struct Cell
        {
            public TileBase Tile;
            public Matrix4x4 Matrix;
            public Color Color;
        }

        private static int Retile(Tilemap tm)
        {
            var bounds = tm.cellBounds;
            var cells = new Dictionary<Vector2Int, Cell>();
            foreach (var p in bounds.allPositionsWithin)
            {
                var t = tm.GetTile(p);
                if (t == null) continue;
                cells[new Vector2Int(p.x, p.y)] = new Cell { Tile = t, Matrix = tm.GetTransformMatrix(p), Color = tm.GetColor(p) };
            }
            if (cells.Count == 0) return 0;

            tm.ClearAllTiles();
            int xMin = bounds.xMin + Math.Min(DxW, 0), xMax = bounds.xMax - 1 + Math.Max(DxE, 0);
            int yMin = bounds.yMin + Math.Min(DyS, 0), yMax = bounds.yMax - 1;
            int placed = 0;
            for (int y = yMin; y <= yMax; y++)
            for (int x = xMin; x <= xMax; x++)
            {
                if (!cells.TryGetValue(new Vector2Int(MapX(x), MapY(y)), out var c)) continue;
                var p = new Vector3Int(x, y, 0);
                tm.SetTile(p, c.Tile);
                if (c.Matrix != Matrix4x4.identity || c.Color != Color.white)
                {
                    tm.SetTileFlags(p, TileFlags.None);
                    tm.SetTransformMatrix(p, c.Matrix);
                    tm.SetColor(p, c.Color);
                }
                placed++;
            }
            return placed;
        }

        private static Rect StretchRect(Bounds b) =>
            Rect.MinMaxRect(StretchX(b.min.x), StretchY(b.min.y), StretchX(b.max.x), StretchY(b.max.y));

        // The boundaries are rotated, scaled boxes; replace each with a plain box of the new size.
        private static void StretchCollider(Transform t)
        {
            var col = t.GetComponent<BoxCollider2D>();
            if (col == null) return;
            var r = StretchRect(col.bounds);
            t.rotation = Quaternion.identity;
            t.localScale = Vector3.one;
            t.position = new Vector3(r.center.x, r.center.y, t.position.z);
            col.offset = Vector2.zero;
            col.size = r.size;
        }

        private static void StretchSprite(Transform t)
        {
            var sr = t.GetComponent<SpriteRenderer>();
            if (sr == null || sr.sprite == null) return;
            var r = StretchRect(sr.bounds);
            var sb = sr.sprite.bounds;
            t.rotation = Quaternion.identity;
            var scale = new Vector3(r.width / sb.size.x, r.height / sb.size.y, 1f);
            t.localScale = scale;
            t.position = new Vector3(r.center.x - sb.center.x * scale.x, r.center.y - sb.center.y * scale.y, t.position.z);
        }

        private static void StretchLight(UnityEngine.Rendering.Universal.Light2D light)
        {
            var path = light.shapePath;
            if (path == null || path.Length == 0) return;
            var tr = light.transform;
            var moved = new Vector3[path.Length];
            for (int i = 0; i < path.Length; i++)
            {
                var w = tr.TransformPoint(path[i]);
                moved[i] = tr.InverseTransformPoint(new Vector3(StretchX(w.x), StretchY(w.y), w.z));
            }
            light.SetShapePath(moved);
        }

        // Layout 5's camera frames its walls with ~1 unit to spare above and below (size 10 for
        // 18 rows). Keep that framing, and zoom out further only when the room is too wide.
        private static void FitCamera(GameObject layout)
        {
            var cam = layout.GetComponentInChildren<Camera>(false);
            if (cam == null) return;
            float left = X0 - 1.5f, right = X1 + 1.5f, bottom = Y0 - 1.5f, top = TopY + 2.5f;
            float aspect = cam.aspect > 0.1f ? cam.aspect : 16f / 9f;
            float size = Mathf.Max((top - bottom) / 2f + 1f, ((right - left) / 2f + 1f) / aspect);
            cam.orthographicSize = size;
            cam.transform.position = new Vector3((left + right) / 2f - 0.23f, (top + bottom) / 2f + 0.21f, cam.transform.position.z);
        }

        /// <summary>
        /// GameController.CheckFurnitureBounds for the custom room: furniture outside the floor goes
        /// back to the inventory and assistants outside are unsummoned, as the game does itself.
        /// </summary>
        internal static void ClearOutside()
        {
            float minX = X0, maxX = X1, maxY = TopY, minY = Y0 - 0.5f;
            bool Outside(Vector3 p) => p.x < minX || p.x > maxX || p.y > maxY || p.y < minY;

            foreach (var item in UnityEngine.Object.FindObjectsOfType<KitchenLayoutItem>())
            {
                if (!Outside(item.transform.position)) continue;
                UnityEngine.Object.Destroy(item.gameObject);
                GameData.Instance.AdjustFurnitureCountInInventory(item.GetID(), 1);
            }
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AssistantController>())
                if (Outside(a.transform.position)) a.UnSummon(silent: true);
        }
    }
}
