using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

using UnityEngine;

using Common.Core.Numerics;
using Common.Core.Colors;
using Common.Core.Shapes;
using Common.Core.Directions;
using Common.Core.Extensions;
using Common.Core.Threading;
using Common.GraphTheory.GridGraphs;

using ImageProcessing.Images;

namespace AperiodicTexturing
{

    public static partial class ImageSynthesis
    {

        public static void CreateWangTileImage(WangTileSet tileSet, IList<Tile> tileables, ExemplarSet exemplarSet, ThreadingToken token = null)
        {
            var tiles = tileSet.ToFlattenedList();

            if (token != null)
            {
                token.EnqueueMessage("Stage 1 of 1");
                token.Steps = tiles.Count;
            }
                
            int blockSize = ThreadingBlock1D.BlockSize(tiles.Count, 8);

            ThreadingBlock1D.ParallelAction(tiles.Count, blockSize, (i) =>
            {
                CreateWangTileImageStage1(tiles[i], tileables, exemplarSet);

            }, token);

        }

        private static void CreateWangTileImageStage1(WangTile wtile, IList<Tile> tileables, ExemplarSet set)
        {
            var colors = GetSortedColors(wtile);
            var rng = new System.Random(0);

            for (int i = 0; i < colors.Count; i++)
            {
                if(i == 0)
                {
                    int color = colors[i];
                    wtile.Fill(tileables[color].Images);
                }
                else
                {
                    int color = colors[i];
                    var tile = wtile.Tile;

                    var map = CreateMap(wtile);
                    var mask = CreateMask(map, color, 3);

                    FillFromMap(map, tile.Image, tileables);

                    var points = GetMaskedPoints_WANG(mask);
                    points.Shuffle(rng);

                    FillWithRandom_WANG(0, points, tile.Image, set, rng);

                    FillPatches_WANG(points, set, mask, tile.Image);

                    //tile.Image.SaveAsRaw(DEUB_FOLDER + "tile" + wtile.Index1);
                    //map.SaveAsRaw(DEUB_FOLDER + "map" + wtile.Index1);
                    //mask.SaveAsRaw(DEUB_FOLDER + "mask" + wtile.Index1)

                }
            }

        }

        private static List<Point2i> GetMaskedPoints_WANG(BinaryImage2D mask)
        {
            var points = new List<Point2i>();
            mask.Iterate((x, y) =>
            {
                if (mask[x, y])
                {
                    points.Add(new Point2i(x, y));
                }
            });

            return points;
        }

        private static void FillWithRandom_WANG(int index, List<Point2i> points, ColorImage2D image, ExemplarSet set, System.Random rng)
        {
            foreach (var p in points)
            {
                var pixel = set.GetRandomSourcePixel(index, rng);
                image[p.x, p.y] = pixel;
            }

        }

        private static void FillPatches_WANG(List<Point2i> points, ExemplarSet set, BinaryImage2D mask, ColorImage2D image)
        {
            int exemplarSize = set.ExemplarSize;
            int halfExemplarSize = exemplarSize / 2;
            int quaterExemplarSize = exemplarSize / 4;

            foreach (var p in points)
            {
                int x = p.x;
                int y = p.y;

                if (x < quaterExemplarSize || x > image.Width - quaterExemplarSize - 1)
                    continue;

                if (y < quaterExemplarSize || y > image.Height - quaterExemplarSize - 1)
                    continue;

                if (!mask[x, y]) continue;

                var box = new Box2i(x - halfExemplarSize, y - halfExemplarSize, x + halfExemplarSize, y + halfExemplarSize);
                var crop = ColorImage2D.Crop(image, box, 0, WRAP_MODE.WRAP);

                var match = FindBestMatch_TEST(crop, set, mask);

                var graph = PerformGraphCut_TEST(crop, match, halfExemplarSize);

                var blendMask = CreateMaskFromGraph_TEST(graph, 2, 0.5f);

                var blendedImage = BlendImages_TEST(graph, crop, match, blendMask);

                var m = blendMask.ToBinaryImage();
                m.Invert();

                image.Fill(blendedImage, box, WRAP_MODE.WRAP);
                mask.Fill(box, m, false, WRAP_MODE.WRAP);

            };
        }

        private static List<int> GetSortedColors(WangTile tile)
        {
            var colors = new List<int>();

            foreach (var color in tile.Edges)
            {
                if (!colors.Contains(color))
                    colors.Add(color);
            }

            colors.Sort();

            return colors;
        }

        private static GreyScaleImage2D CreateMap(WangTile tile)
        {
            int width = tile.Width;
            int height = tile.Height;
            var map = new GreyScaleImage2D(width, height);

            if (tile.IsConst)
            {
                map.Fill(tile.Left);
            }
            else
            {
                var c00 = new Point2i(0, 0);
                var c01 = new Point2i(0, height);
                var c10 = new Point2i(width, 0);
                var c11 = new Point2i(width, height);
                var mid = new Point2i(width / 2, height / 2);

                map.DrawTriangle(mid, c00, c01, new ColorRGBA(tile.Left, 1), true);
                map.DrawTriangle(mid, c00, c10, new ColorRGBA(tile.Bottom, 1), true);
                map.DrawTriangle(mid, c10, c11, new ColorRGBA(tile.Right, 1), true);
                map.DrawTriangle(mid, c01, c11, new ColorRGBA(tile.Top, 1), true);
            }

            return map;
        }

        private static void FillFromMap(GreyScaleImage2D map, ColorImage2D image, IList<Tile> tileables)
        {
            image.Fill((x, y) =>
            {
                int index = (int)map[x, y];
                return tileables[index].Image[x, y];
            });
        }

        private static BinaryImage2D CreateMask(GreyScaleImage2D map, int color, int thickness)
        {
            var mask = new BinaryImage2D(map.Size);

            mask.Iterate((x, y) =>
            {
                if (map[x, y] != color) return;

                for (int i = 0; i < 8; i++)
                {
                    int xi = x + D8.OFFSETS[i, 0];
                    int yi = y + D8.OFFSETS[i, 1];

                    if (mask.NotInBounds(xi, yi)) continue;

                    if (map[xi, yi] != color)
                    {
                        mask[x, y] = true;
                        break;
                    }
                }
            });

            mask = BinaryImage2D.Dilate(mask, thickness);

            var bounds = mask.Bounds;
            var corners = bounds.GetCorners();

            foreach(var p in bounds.EnumeratePerimeter())
            {
                if (!corners.Contains(p))
                    mask[p.x, p.y] = false;
            }

            return mask;
        }

    }
}
