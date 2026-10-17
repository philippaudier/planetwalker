using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Interleaved vertex of an adaptive terrain patch (V0.2.0E; 84 bytes since V0.2.0F.2). Float32 where precision matters
    /// (position, normal, color: unchanged from V0.2.0D; grid position and height above sea level, which the strata
    /// read to the centimeter), half precision for the coarser level's normal and the downslope direction, 8-bit
    /// normalized for the geology weights and the province rock color.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        /// <summary>TANGENT: normal of the next coarser level (xyz) and the rock family field (w, -1 .. 1) (half x4).</summary>
        public ushort CoarseX, CoarseY, CoarseZ, CoarseW;
        public Color Color;
        /// <summary>TEXCOORD0: grid u, grid v, height above sea level (m), reference slope.</summary>
        public Vector4 Grid;
        /// <summary>TEXCOORD1: bedrock, sediment, erosion, strata (UNorm8 x4).</summary>
        public uint Geology;
        /// <summary>TEXCOORD2: downslope direction xyz, concavity (half x4).</summary>
        public ushort FlowX, FlowY, FlowZ, FlowW;
        /// <summary>TEXCOORD3: rock color rgb, sedimentary weight (UNorm8 x4).</summary>
        public uint RockTint;
        /// <summary>TEXCOORD4: ground cover: green grass, dry grass, forest floor, snow (UNorm8 x4; V0.2.0F.2).</summary>
        public uint Cover;

        public const int Size = 84;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float16, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.UNorm8, 4),
        };

        /// <summary>Packs a built patch (one vertex per element of <paramref name="target"/>).</summary>
        public static void Pack(PatchGeometryData data, TerrainVertex[] target, int count)
        {
            for (int v = 0; v < count; v++)
            {
                Vector3 coarse = data.CoarseNormals[v];
                Vector4 flow = data.Flow[v];
                Color rock = data.RockTints[v];
                target[v] = new TerrainVertex
                {
                    Position = data.Vertices[v],
                    Normal = data.Normals[v],
                    CoarseX = ToHalf(coarse.x),
                    CoarseY = ToHalf(coarse.y),
                    CoarseZ = ToHalf(coarse.z),
                    CoarseW = ToHalf(data.RockFamily[v]),
                    Color = data.Colors[v],
                    Grid = data.Grid[v],
                    Geology = PackUnorm(data.Geology[v]),
                    FlowX = ToHalf(flow.x),
                    FlowY = ToHalf(flow.y),
                    FlowZ = ToHalf(flow.z),
                    FlowW = ToHalf(flow.w),
                    RockTint = PackUnorm(new Vector4(rock.r, rock.g, rock.b, rock.a)),
                    Cover = PackUnorm(data.Cover[v]),
                };
            }
        }

        static uint PackUnorm(Vector4 v)
        {
            return Byte(v.x) | (Byte(v.y) << 8) | (Byte(v.z) << 16) | (Byte(v.w) << 24);
        }

        static uint Byte(float x)
        {
            float c = x < 0f ? 0f : x > 1f ? 1f : x;
            return (uint)(c * 255f + 0.5f);
        }

        /// <summary>IEEE 754 binary16 bits of a float (round to nearest even; overflow to infinity, tiny values to zero).</summary>
        public static ushort ToHalf(float value)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(value);
            uint sign = (bits >> 16) & 0x8000u;
            int exponent = (int)((bits >> 23) & 0xFF) - 127 + 15;
            uint mantissa = bits & 0x7FFFFFu;
            if (((bits >> 23) & 0xFF) == 0xFF)
            {
                return (ushort)(sign | 0x7C00u | (mantissa != 0 ? 0x200u : 0u)); // inf / nan
            }
            if (exponent >= 31)
            {
                return (ushort)(sign | 0x7C00u);
            }
            if (exponent <= 0)
            {
                if (exponent < -10)
                {
                    return (ushort)sign;
                }
                mantissa |= 0x800000u;
                int shift = 14 - exponent;
                uint half = mantissa >> shift;
                uint rest = mantissa & ((1u << shift) - 1u);
                uint halfway = 1u << (shift - 1);
                if (rest > halfway || (rest == halfway && (half & 1u) != 0))
                {
                    half++;
                }
                return (ushort)(sign | half);
            }
            uint h = sign | ((uint)exponent << 10) | (mantissa >> 13);
            uint r = mantissa & 0x1FFFu;
            if (r > 0x1000u || (r == 0x1000u && (h & 1u) != 0))
            {
                h++; // may carry into the exponent: still correct
            }
            return (ushort)h;
        }
    }
}
