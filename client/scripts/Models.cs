using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;

namespace IH.Client;

/// <summary>Builds low-poly meshes out of coloured triangles; the transform lets a part be placed, turned and scaled.</summary>
public sealed class MeshBuilder
{
    private readonly SurfaceTool _st = new();
    public int Count;
    /// <summary>Applied to everything added from now on.</summary>
    public Transform3D Xf = Transform3D.Identity;
    /// <summary>Applied to every colour (fog of war darkens remembered tiles).</summary>
    public Func<Color, Color>? Tint;
    /// <summary>Darken vertices close to the local ground plane to ground the models under ambient lighting.</summary>
    public float ContactShade;

    public MeshBuilder() => _st.Begin(Mesh.PrimitiveType.Triangles);

    /// <summary>Adds a triangle whose lit side faces roughly along <paramref name="outward"/>.</summary>
    public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Color color)
    {
        a = Xf * a; b = Xf * b; c = Xf * c; outward = Xf.Basis * outward;
        if (Tint != null) color = Tint(color);
        var n = (c - a).Cross(b - a);
        if (n.LengthSquared() < 1e-12f) return;
        n = n.Normalized();
        if (n.Dot(outward) < 0) { (b, c) = (c, b); n = -n; }
        Color Lit(Vector3 p) => ContactShade <= 0 ? color
            : color.Darkened(ContactShade * (1f - Mathf.Clamp(p.Y / 0.19f, 0f, 1f)));
        _st.SetNormal(n); _st.SetColor(Lit(a)); _st.AddVertex(a);
        _st.SetNormal(n); _st.SetColor(Lit(b)); _st.AddVertex(b);
        _st.SetNormal(n); _st.SetColor(Lit(c)); _st.AddVertex(c);
        Count += 3;
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Color color)
    {
        Tri(a, b, c, outward, color);
        Tri(a, c, d, outward, color);
    }

    public void Pyramid(Vector3 baseCenter, float radius, float height, int sides, Color color, float turn = 0)
    {
        var apex = baseCenter + new Vector3(0, height, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = turn + Mathf.Tau * i / sides, a1 = turn + Mathf.Tau * (i + 1) / sides;
            var p0 = baseCenter + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius;
            var p1 = baseCenter + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
            var mid = (p0 + p1) / 2 - baseCenter;
            Tri(p0, p1, apex, mid + new Vector3(0, radius * 0.5f * Math.Sign(height), 0), color);
        }
    }

    public void Box(Vector3 center, Vector3 size, Color color)
    {
        var h = size / 2;
        Vector3 P(int x, int y, int z) => center + new Vector3(h.X * x, h.Y * y, h.Z * z);
        Quad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), Vector3.Up, color);
        Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), Vector3.Back, color);
        Quad(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), Vector3.Forward, color);
        Quad(P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), Vector3.Right, color);
        Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), Vector3.Left, color);
    }

    /// <summary>A column that may taper: a trunk, a barrel, a tower, a cone with its tip cut off.</summary>
    public void Column(Vector3 baseCenter, float radius, float height, Color color, int sides = 6, float topRadius = -1)
    {
        if (topRadius < 0) topRadius = radius;
        var top = baseCenter + new Vector3(0, height, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            Vector3 d0 = new(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            Quad(baseCenter + d0 * radius, baseCenter + d1 * radius, top + d1 * topRadius, top + d0 * topRadius, d0 + d1, color);
            if (topRadius > 0.001f) Tri(top, top + d0 * topRadius, top + d1 * topRadius, Vector3.Up, color.Lightened(0.06f));
        }
    }

    /// <summary>A faceted ball or gem: two pyramids base to base.</summary>
    public void Gem(Vector3 center, float radius, Color color, float stretch = 1, int sides = 6)
    {
        Pyramid(center, radius, radius * stretch, sides, color.Lightened(0.08f));
        Pyramid(center, radius, -radius * stretch, sides, color.Darkened(0.15f));
    }

    public Mesh? Commit() => Count == 0 ? null : _st.Commit();
}

/// <summary>
/// The look of things on the map. Every resource and every kind of unit has its own small model, so a glance
/// tells wheat from iron and a spearman from an archer without reading labels.
/// </summary>
public static class Models
{
    private static Color C(int rgb) => Ui.Col(rgb);
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    // ------------------------------------------------------------ resources
    /// <summary>Draws the resource standing on a small plate coloured by its class (bonus, strategic, luxury).</summary>
    public static void Resource(MeshBuilder b, Resource r, Vector3 at, Color classColor)
    {
        var saved = b.Xf;
        b.Xf = saved * new Transform3D(Basis.Identity.Scaled(Vector3.One * 1.45f), at);
        // a thin rim in the class colour; the model itself carries the meaning
        b.Column(V(0, 0, 0), 0.25f, 0.02f, classColor.Darkened(0.1f), 8);
        b.Column(V(0, 0.02f, 0), 0.215f, 0.006f, classColor.Lerp(C(0x3A4030), 0.72f), 8);
        var o = V(0, 0.025f, 0);

        void Rocks(Color main, Color accent)
        {
            b.Pyramid(o + V(-0.07f, 0, 0.02f), 0.15f, 0.24f, 5, main, 0.4f);
            b.Pyramid(o + V(0.1f, 0, -0.04f), 0.11f, 0.17f, 5, main.Darkened(0.12f), 1.1f);
            b.Pyramid(o + V(0.03f, 0, 0.12f), 0.08f, 0.11f, 4, main.Lightened(0.1f));
            b.Box(o + V(-0.05f, 0.12f, 0.1f), V(0.05f, 0.05f, 0.05f), accent);
            b.Box(o + V(0.1f, 0.09f, 0.03f), V(0.04f, 0.04f, 0.04f), accent);
        }
        void Crystals(Color col)
        {
            b.Gem(o + V(-0.04f, 0.17f, 0), 0.09f, col, 1.9f, 5);
            b.Gem(o + V(0.1f, 0.1f, 0.06f), 0.06f, col.Lightened(0.15f), 1.7f, 5);
            b.Gem(o + V(0.02f, 0.08f, -0.1f), 0.05f, col.Darkened(0.1f), 1.6f, 5);
        }
        void Tree(Color crown, Color fruit, bool round = true)
        {
            b.Column(o, 0.035f, 0.16f, C(0x6B4A2C), 5);
            if (round) b.Gem(o + V(0, 0.27f, 0), 0.17f, crown, 0.8f, 7);
            else b.Pyramid(o + V(0, 0.12f, 0), 0.17f, 0.3f, 6, crown);
            foreach (var p in new[] { V(0.13f, 0.27f, 0.09f), V(-0.12f, 0.3f, 0.08f), V(0.02f, 0.2f, 0.16f), V(-0.02f, 0.36f, 0.06f) })
                b.Gem(o + p, 0.04f, fruit);
        }
        void Bush(Color leaf, Color dots, int rows = 2)
        {
            for (int i = 0; i < rows; i++)
                for (int k = -1; k <= 1; k++)
                {
                    var p = o + V(k * 0.13f, 0, (i - (rows - 1) / 2f) * 0.16f);
                    b.Gem(p + V(0, 0.07f, 0), 0.085f, leaf, 0.8f, 6);
                    b.Gem(p + V(0.02f, 0.15f, 0.02f), 0.03f, dots);
                }
        }
        void Animal(Color body, Color head, float len, float tall, Color? horn = null, bool antlers = false)
        {
            float leg = tall * 0.45f;
            foreach (var (lx, lz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                b.Box(o + V(lx * len * 0.36f, leg / 2, lz * 0.045f), V(0.035f, leg, 0.035f), body.Darkened(0.3f));
            b.Box(o + V(0, leg + tall * 0.27f, 0), V(len, tall * 0.55f, 0.13f), body);
            b.Box(o + V(len * 0.58f, leg + tall * 0.55f, 0), V(0.1f, 0.1f, 0.09f), head);
            if (horn != null)
            {
                b.Box(o + V(len * 0.58f, leg + tall * 0.66f, 0.06f), V(0.025f, 0.07f, 0.025f), horn.Value);
                b.Box(o + V(len * 0.58f, leg + tall * 0.66f, -0.06f), V(0.025f, 0.07f, 0.025f), horn.Value);
            }
            if (antlers)
                foreach (int s in new[] { -1, 1 })
                {
                    b.Box(o + V(len * 0.56f, leg + tall * 0.78f, s * 0.05f), V(0.02f, 0.14f, 0.02f), C(0xE9DFC4));
                    b.Box(o + V(len * 0.56f, leg + tall * 0.84f, s * 0.09f), V(0.02f, 0.02f, 0.08f), C(0xE9DFC4));
                }
        }
        void Fish(Vector3 p, Color col, float size)
        {
            b.Gem(p, size, col, 0.55f, 4);
            b.Tri(p + V(-size * 0.8f, 0, 0), p + V(-size * 1.6f, size * 0.5f, 0), p + V(-size * 1.6f, -size * 0.5f, 0), Vector3.Back, col.Darkened(0.2f));
            b.Tri(p + V(-size * 0.8f, 0, 0), p + V(-size * 1.6f, size * 0.5f, 0), p + V(-size * 1.6f, -size * 0.5f, 0), Vector3.Forward, col.Darkened(0.2f));
        }
        void Stalks(Color stem, Color tip, float h, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.Tau * i / n;
                var p = o + V(Mathf.Cos(a) * 0.1f, 0, Mathf.Sin(a) * 0.1f);
                b.Column(p, 0.02f, h, stem, 4);
                b.Gem(p + V(0, h + 0.03f, 0), 0.04f, tip, 1.6f, 4);
            }
        }
        void Ingots(Color col)
        {
            b.Box(o + V(-0.07f, 0.035f, 0.03f), V(0.2f, 0.07f, 0.1f), col);
            b.Box(o + V(0.08f, 0.035f, -0.07f), V(0.2f, 0.07f, 0.1f), col.Darkened(0.1f));
            b.Box(o + V(0, 0.105f, -0.02f), V(0.2f, 0.07f, 0.1f), col.Lightened(0.12f));
        }

        switch (r)
        {
            case IH.Shared.Resource.Bananas:
                b.Column(o, 0.03f, 0.3f, C(0x7A5A34), 5);
                for (int i = 0; i < 5; i++)
                {
                    float a = Mathf.Tau * i / 5;
                    b.Tri(o + V(0, 0.3f, 0), o + V(Mathf.Cos(a) * 0.22f, 0.24f, Mathf.Sin(a) * 0.22f), o + V(Mathf.Cos(a + 0.5f) * 0.14f, 0.33f, Mathf.Sin(a + 0.5f) * 0.14f), Vector3.Up, C(0x3E9A3A));
                }
                b.Box(o + V(0.06f, 0.2f, 0.03f), V(0.05f, 0.11f, 0.05f), C(0xF4D53A));
                b.Box(o + V(-0.04f, 0.19f, 0.05f), V(0.05f, 0.1f, 0.05f), C(0xE6C22E));
                break;
            case IH.Shared.Resource.Stone:
                b.Box(o + V(-0.05f, 0.06f, 0), V(0.24f, 0.12f, 0.2f), C(0xA9A9A2));
                b.Box(o + V(0.08f, 0.05f, 0.1f), V(0.16f, 0.1f, 0.14f), C(0x8E8E88));
                b.Box(o + V(-0.02f, 0.17f, -0.01f), V(0.15f, 0.1f, 0.14f), C(0xBDBDB6));
                break;
            case IH.Shared.Resource.Crabs:
                b.Box(o + V(0, 0.06f, 0), V(0.2f, 0.07f, 0.15f), C(0xE2553A));
                foreach (int s in new[] { -1, 1 })
                {
                    b.Box(o + V(s * 0.15f, 0.09f, 0.1f), V(0.08f, 0.06f, 0.08f), C(0xF06A4A));
                    b.Box(o + V(s * 0.13f, 0.03f, -0.04f), V(0.1f, 0.02f, 0.02f), C(0xB8402C));
                    b.Box(o + V(s * 0.05f, 0.12f, 0.06f), V(0.02f, 0.05f, 0.02f), C(0x1A1A1A));
                }
                break;
            case IH.Shared.Resource.Maize: Stalks(C(0x4FA83C), C(0xF2C230), 0.26f, 4); break;
            case IH.Shared.Resource.Wheat: Stalks(C(0xD9B94A), C(0xF4DC7A), 0.2f, 6); break;
            case IH.Shared.Resource.Rice:
                b.Column(o, 0.22f, 0.02f, C(0x6FB8D8), 8);
                Stalks(C(0x8FD46A), C(0xF2F0D8), 0.13f, 6);
                break;
            case IH.Shared.Resource.Copper: Rocks(C(0x9A6A4A), C(0x3FB89A)); break;
            case IH.Shared.Resource.Iron: Rocks(C(0x5A5F6A), C(0xC8643A)); break;
            case IH.Shared.Resource.Coal: Rocks(C(0x1E1E22), C(0x4A4A52)); break;
            case IH.Shared.Resource.Silver: Rocks(C(0x8E949C), C(0xF2F4F8)); break;
            case IH.Shared.Resource.Mercury: Rocks(C(0xB23A32), C(0xDCE4EC)); break;
            case IH.Shared.Resource.Sheep: Animal(C(0xF4F1E6), C(0x3A3530), 0.22f, 0.2f); break;
            case IH.Shared.Resource.Cattle: Animal(C(0x8A5A3A), C(0xE8DCC8), 0.28f, 0.24f, C(0xF2ECD8)); break;
            case IH.Shared.Resource.Deer: Animal(C(0xB07A48), C(0xB07A48), 0.22f, 0.26f, null, true); break;
            case IH.Shared.Resource.Horses:
                Animal(C(0x5A3A26), C(0x5A3A26), 0.26f, 0.3f);
                b.Box(o + V(0.11f, 0.3f, 0), V(0.07f, 0.14f, 0.07f), C(0x5A3A26));
                b.Box(o + V(0.08f, 0.33f, 0), V(0.03f, 0.14f, 0.03f), C(0x1E1410));
                b.Box(o + V(-0.15f, 0.2f, 0), V(0.03f, 0.14f, 0.03f), C(0x1E1410));
                break;
            case IH.Shared.Resource.Furs:
                Animal(C(0xD0702E), C(0xD0702E), 0.2f, 0.15f);
                b.Box(o + V(-0.17f, 0.13f, 0), V(0.13f, 0.06f, 0.06f), C(0xF2E6D0));
                break;
            case IH.Shared.Resource.Ivory:
                Animal(C(0x8C8F96), C(0x8C8F96), 0.28f, 0.32f);
                b.Box(o + V(0.21f, 0.14f, 0), V(0.04f, 0.2f, 0.04f), C(0x7C7F86));
                foreach (int s in new[] { -1, 1 }) b.Box(o + V(0.24f, 0.2f, s * 0.05f), V(0.1f, 0.025f, 0.025f), C(0xFFFBEA));
                break;
            case IH.Shared.Resource.Fish:
                Fish(o + V(0.02f, 0.1f, 0.06f), C(0x8FC4E8), 0.1f);
                Fish(o + V(0.04f, 0.2f, -0.06f), C(0xC8DCEC), 0.075f);
                break;
            case IH.Shared.Resource.Whales:
                b.Gem(o + V(0, 0.09f, 0), 0.19f, C(0x2E4A72), 0.45f, 6);
                b.Tri(o + V(-0.17f, 0.09f, 0), o + V(-0.3f, 0.22f, 0.07f), o + V(-0.3f, 0.22f, -0.07f), Vector3.Up, C(0x24395A));
                b.Column(o + V(0.07f, 0.16f, 0), 0.012f, 0.12f, C(0xEAF6FF), 4);
                b.Gem(o + V(0.07f, 0.3f, 0), 0.04f, C(0xEAF6FF));
                break;
            case IH.Shared.Resource.Turtles:
                b.Pyramid(o + V(0, 0.04f, 0), 0.15f, 0.11f, 6, C(0x4E8A3E));
                b.Column(o + V(0, 0.02f, 0), 0.15f, 0.02f, C(0x6FA85A), 6);
                b.Box(o + V(0.18f, 0.05f, 0), V(0.08f, 0.05f, 0.06f), C(0x9AC27A));
                foreach (var (fx, fz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }) b.Box(o + V(fx * 0.1f, 0.025f, fz * 0.14f), V(0.06f, 0.02f, 0.06f), C(0x9AC27A));
                break;
            case IH.Shared.Resource.Pearls:
                b.Tri(o + V(-0.16f, 0.02f, -0.12f), o + V(0.16f, 0.02f, -0.12f), o + V(0, 0.02f, 0.14f), Vector3.Up, C(0xC8B8D8));
                b.Tri(o + V(-0.16f, 0.02f, -0.12f), o + V(0.16f, 0.02f, -0.12f), o + V(0, 0.24f, 0.02f), Vector3.Back, C(0xE8DCF0));
                b.Tri(o + V(-0.16f, 0.02f, -0.12f), o + V(0.16f, 0.02f, -0.12f), o + V(0, 0.24f, 0.02f), Vector3.Forward, C(0xA898B8));
                b.Gem(o + V(0, 0.07f, 0), 0.055f, C(0xFFFFFF), 1, 8);
                break;
            case IH.Shared.Resource.Niter:
                b.Pyramid(o + V(-0.06f, 0, 0), 0.14f, 0.12f, 6, C(0xEDE8C4));
                b.Pyramid(o + V(0.1f, 0, 0.06f), 0.1f, 0.09f, 6, C(0xDCD6A8));
                b.Box(o + V(0.02f, 0.03f, -0.13f), V(0.07f, 0.06f, 0.07f), C(0xF8F4DC));
                break;
            case IH.Shared.Resource.Salt:
                b.Pyramid(o + V(-0.07f, 0, 0.02f), 0.12f, 0.2f, 8, C(0xFAFAFA));
                b.Pyramid(o + V(0.1f, 0, -0.03f), 0.09f, 0.14f, 8, C(0xE8ECF0));
                break;
            case IH.Shared.Resource.Oil:
                b.Column(o, 0.2f, 0.015f, C(0x0E0E12), 8);
                b.Pyramid(o + V(0, 0.2f, 0), 0.07f, 0.14f, 6, C(0x15151A));
                b.Gem(o + V(0, 0.16f, 0), 0.075f, C(0x15151A), 0.9f);
                break;
            case IH.Shared.Resource.Aluminium: Ingots(C(0xBFD0DC)); break;
            case IH.Shared.Resource.Gypsum:
                b.Box(o + V(0, 0.03f, 0), V(0.3f, 0.05f, 0.22f), C(0xF0EEE6));
                b.Box(o + V(0.02f, 0.085f, 0.01f), V(0.24f, 0.05f, 0.18f), C(0xE0DCD0));
                b.Box(o + V(-0.01f, 0.14f, 0), V(0.17f, 0.05f, 0.13f), C(0xFAF8F2));
                break;
            case IH.Shared.Resource.Uranium: Crystals(C(0x7CFF4A)); break;
            case IH.Shared.Resource.Diamonds: Crystals(C(0xBFF4FF)); break;
            case IH.Shared.Resource.Jade: Crystals(C(0x2FA873)); break;
            case IH.Shared.Resource.Amber: Crystals(C(0xF29A1F)); break;
            case IH.Shared.Resource.Incense:
                b.Column(o, 0.08f, 0.07f, C(0x9A6A3A), 6, 0.12f);
                b.Column(o + V(0, 0.07f, 0), 0.02f, 0.14f, C(0xD8D8E0), 4, 0.035f);
                b.Gem(o + V(0.03f, 0.27f, 0), 0.05f, C(0xECECF4));
                b.Gem(o + V(-0.02f, 0.36f, 0.02f), 0.035f, C(0xF6F6FA));
                break;
            case IH.Shared.Resource.Wine:
                b.Column(o, 0.015f, 0.3f, C(0x6B4A2C), 4);
                b.Box(o + V(0, 0.3f, 0), V(0.26f, 0.02f, 0.02f), C(0x6B4A2C));
                foreach (var p in new[] { V(-0.08f, 0.22f, 0.02f), V(-0.04f, 0.16f, 0.03f), V(-0.11f, 0.15f, 0.02f), V(0.08f, 0.21f, 0.02f), V(0.1f, 0.14f, 0.03f), V(0.05f, 0.14f, 0.01f) })
                    b.Gem(o + p, 0.045f, C(0x7A2E8A));
                b.Gem(o + V(0.02f, 0.3f, 0.03f), 0.05f, C(0x4FA83C), 0.4f);
                break;
            case IH.Shared.Resource.Cocoa: Tree(C(0x2E7A3A), C(0x7A4420)); break;
            case IH.Shared.Resource.Citrus: Tree(C(0x3E9A3A), C(0xFF9A1F)); break;
            case IH.Shared.Resource.Olives: Tree(C(0x8FA07A), C(0x2E3A22)); break;
            case IH.Shared.Resource.Truffles:
                b.Pyramid(o + V(0.08f, 0.05f, -0.06f), 0.12f, 0.26f, 5, C(0x2E6A34));
                b.Column(o + V(0.08f, 0, -0.06f), 0.025f, 0.06f, C(0x6B4A2C), 4);
                b.Gem(o + V(-0.09f, 0.04f, 0.06f), 0.06f, C(0x3A2A20), 0.8f);
                b.Gem(o + V(0, 0.035f, 0.13f), 0.045f, C(0x4A382A), 0.8f);
                break;
            case IH.Shared.Resource.Coffee: Bush(C(0x2E7A3A), C(0xD0302A)); break;
            case IH.Shared.Resource.Cotton: Bush(C(0x5A9A4A), C(0xFFFFFF)); break;
            case IH.Shared.Resource.Tea: Bush(C(0x1F6A3A), C(0x9AE06A), 3); break;
            case IH.Shared.Resource.Sugar:
                Stalks(C(0x9AC85A), C(0x9AC85A), 0.24f, 3);
                b.Box(o + V(0.02f, 0.04f, 0.13f), V(0.08f, 0.08f, 0.08f), C(0xFFFFFF));
                b.Box(o + V(-0.09f, 0.035f, 0.1f), V(0.07f, 0.07f, 0.07f), C(0xF0F0F0));
                break;
            case IH.Shared.Resource.Tobacco:
                for (int i = 0; i < 5; i++)
                {
                    float a = Mathf.Tau * i / 5;
                    var d = V(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var side = V(-d.Z, 0, d.X) * 0.07f;
                    b.Quad(o + d * 0.03f - side, o + d * 0.03f + side, o + d * 0.2f + side * 0.4f + V(0, 0.22f, 0), o + d * 0.2f - side * 0.4f + V(0, 0.22f, 0), Vector3.Up, C(0x8A9A3A));
                    b.Quad(o + d * 0.03f - side, o + d * 0.03f + side, o + d * 0.2f + side * 0.4f + V(0, 0.22f, 0), o + d * 0.2f - side * 0.4f + V(0, 0.22f, 0), Vector3.Down, C(0x6A7A2A));
                }
                break;
            case IH.Shared.Resource.Dyes:
                b.Column(o + V(-0.12f, 0, 0.03f), 0.07f, 0.11f, C(0xD0302A), 6, 0.05f);
                b.Column(o + V(0.04f, 0, -0.08f), 0.07f, 0.13f, C(0x2E5AD0), 6, 0.05f);
                b.Column(o + V(0.1f, 0, 0.1f), 0.06f, 0.09f, C(0xF2C230), 6, 0.045f);
                break;
            case IH.Shared.Resource.Spices:
                b.Column(o + V(-0.1f, 0, 0), 0.08f, 0.09f, C(0xC8B48A), 6, 0.07f);
                b.Pyramid(o + V(-0.1f, 0.09f, 0), 0.07f, 0.09f, 6, C(0xE0532A));
                b.Column(o + V(0.08f, 0, 0.06f), 0.08f, 0.09f, C(0xC8B48A), 6, 0.07f);
                b.Pyramid(o + V(0.08f, 0.09f, 0.06f), 0.07f, 0.09f, 6, C(0xF2B01F));
                b.Column(o + V(0.03f, 0, -0.13f), 0.06f, 0.07f, C(0xC8B48A), 6, 0.05f);
                b.Pyramid(o + V(0.03f, 0.07f, -0.13f), 0.05f, 0.07f, 6, C(0x7A3A1A));
                break;
            case IH.Shared.Resource.Silk:
                b.Column(o, 0.02f, 0.26f, C(0x6B4A2C), 4);
                b.Box(o + V(0, 0.2f, 0), V(0.3f, 0.02f, 0.02f), C(0x6B4A2C));
                b.Gem(o + V(-0.11f, 0.14f, 0), 0.045f, C(0xFFFDF2), 1.5f);
                b.Gem(o + V(0.11f, 0.14f, 0), 0.045f, C(0xFFFDF2), 1.5f);
                b.Quad(o + V(-0.05f, 0.2f, 0.02f), o + V(0.05f, 0.2f, 0.02f), o + V(0.07f, 0.04f, 0.08f), o + V(-0.07f, 0.04f, 0.08f), Vector3.Back, C(0xF08AB8));
                break;
            case IH.Shared.Resource.Marble:
                b.Column(o + V(-0.08f, 0, 0), 0.06f, 0.28f, C(0xF4F2EC), 8);
                b.Box(o + V(-0.08f, 0.3f, 0), V(0.17f, 0.04f, 0.17f), C(0xE4E0D6));
                b.Column(o + V(0.1f, 0, 0.05f), 0.06f, 0.11f, C(0xE8E6E0), 8);
                break;
            case IH.Shared.Resource.Cloves: Bush(C(0x3E7A4A), C(0x7A2A3A)); break;
            case IH.Shared.Resource.Cinnamon:
                foreach (var (dx, dz, h) in new[] { (-0.08f, 0f, 0.26f), (0.02f, 0.05f, 0.32f), (0.1f, -0.03f, 0.22f) })
                    b.Column(o + V(dx, 0, dz), 0.035f, h, C(0xB8632A), 6);
                b.Box(o + V(0.01f, 0.1f, 0.01f), V(0.26f, 0.025f, 0.12f), C(0xE8DCC0));
                break;
            case IH.Shared.Resource.Honey:
                b.Column(o, 0.15f, 0.08f, C(0xE8A81F), 8, 0.13f);
                b.Column(o + V(0, 0.08f, 0), 0.13f, 0.08f, C(0xF2BC30), 8, 0.1f);
                b.Column(o + V(0, 0.16f, 0), 0.1f, 0.07f, C(0xE8A81F), 8, 0.05f);
                b.Box(o + V(0, 0.05f, 0.14f), V(0.05f, 0.06f, 0.02f), C(0x2A1C0E));
                break;
            default:
                b.Gem(o + V(0, 0.18f, 0), 0.13f, classColor, 1.3f, 4);
                break;
        }
        b.Xf = saved;
    }

    // ----------------------------------------------------------------- units
    private static readonly Dictionary<(string id, int owner), Mesh> UnitCache = new();

    /// <summary>The figure of a unit in its owner's colours; built once per unit kind and owner.</summary>
    public static Mesh Unit(UnitDef def, int owner, Color team)
    {
        if (UnitCache.TryGetValue((def.Id, owner), out var cached)) return cached;
        var b = new MeshBuilder { ContactShade = 0.16f };
        // the plate under the figure shows whose unit it is even when the figure itself is mostly steel or wood
        b.Column(V(0, 0.005f, 0), 0.4f, 0.03f, team.Darkened(0.25f), 10);
        b.Column(V(0, 0.035f, 0), 0.33f, 0.012f, team, 10);
        Build(b, def, team);
        return UnitCache[(def.Id, owner)] = b.Commit()!;
    }

    private static void Build(MeshBuilder b, UnitDef def, Color team)
    {
        Color skin = C(0xE8B88A), steel = C(0xAEB6C2), wood = C(0x7A5232), dark = C(0x2A2A30), cloth = C(0xEDE6D4), gold = C(0xF2CF63);
        float g = 0.047f; // top of the plate

        // a person: legs, a tunic in the team colour, a head; returns the height of the shoulders
        float Man(float x = 0, float scale = 1, Color? tunic = null, Color? legs = null, Color? head = null)
        {
            float s = scale;
            b.Box(V(x - 0.06f * s, g + 0.11f * s, 0), V(0.08f * s, 0.22f * s, 0.1f * s), legs ?? dark);
            b.Box(V(x + 0.06f * s, g + 0.11f * s, 0), V(0.08f * s, 0.22f * s, 0.1f * s), legs ?? dark);
            b.Box(V(x, g + 0.37f * s, 0), V(0.26f * s, 0.3f * s, 0.16f * s), tunic ?? team);
            b.Box(V(x, g + 0.61f * s, 0), V(0.15f * s, 0.15f * s, 0.15f * s), head ?? skin);
            return g + 0.5f * s;
        }
        void Helmet(float x, float s, Color col, bool crest = false)
        {
            b.Box(V(x, g + 0.69f * s, 0), V(0.18f * s, 0.07f * s, 0.18f * s), col);
            if (crest) b.Box(V(x, g + 0.76f * s, 0), V(0.04f * s, 0.08f * s, 0.2f * s), team.Lightened(0.2f));
        }
        void Pole(float x, float z, float from, float to, Color col, float thick = 0.03f) => b.Box(V(x, (from + to) / 2, z), V(thick, to - from, thick), col);
        void Shield(float x, bool round)
        {
            if (round) { b.Xf = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), V(x, g + 0.36f, 0.1f)); b.Column(V(0, 0, 0), 0.13f, 0.035f, team.Lightened(0.15f), 8); b.Column(V(0, 0.035f, 0), 0.04f, 0.02f, steel, 6); b.Xf = Transform3D.Identity; }
            else { b.Box(V(x, g + 0.36f, 0.1f), V(0.2f, 0.3f, 0.04f), team.Lightened(0.15f)); b.Box(V(x, g + 0.36f, 0.125f), V(0.05f, 0.3f, 0.02f), steel); }
        }
        void Bow(float x, float size)
        {
            for (int i = 0; i < 6; i++)
            {
                float a0 = -1.1f + 2.2f * i / 6, a1 = -1.1f + 2.2f * (i + 1) / 6;
                var p0 = V(x + Mathf.Cos(a0) * size * 0.45f, g + 0.4f + Mathf.Sin(a0) * size, 0.1f);
                var p1 = V(x + Mathf.Cos(a1) * size * 0.45f, g + 0.4f + Mathf.Sin(a1) * size, 0.1f);
                var mid = (p0 + p1) / 2;
                b.Box(mid, V(0.04f, Math.Abs(p1.Y - p0.Y) + 0.025f, 0.035f), wood);
            }
            Pole(x + 0.06f, 0.1f, g + 0.4f - size * 0.9f, g + 0.4f + size * 0.9f, cloth, 0.012f);
        }
        void Horse(Color coat, bool armour)
        {
            foreach (var (lx, lz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                b.Box(V(lx * 0.19f, g + 0.12f, lz * 0.07f), V(0.055f, 0.24f, 0.055f), coat.Darkened(0.25f));
            b.Box(V(0, g + 0.32f, 0), V(0.54f, 0.2f, 0.2f), coat);
            if (armour) b.Box(V(0, g + 0.3f, 0), V(0.4f, 0.17f, 0.22f), team.Darkened(0.1f));
            b.Box(V(0.26f, g + 0.47f, 0), V(0.1f, 0.24f, 0.12f), coat);
            b.Box(V(0.34f, g + 0.58f, 0), V(0.18f, 0.1f, 0.1f), coat);
            b.Box(V(0.22f, g + 0.52f, 0), V(0.035f, 0.2f, 0.04f), dark);
            b.Box(V(-0.29f, g + 0.3f, 0), V(0.04f, 0.2f, 0.04f), dark);
            // rider
            b.Box(V(-0.03f, g + 0.55f, 0), V(0.2f, 0.26f, 0.15f), team);
            b.Box(V(-0.03f, g + 0.76f, 0), V(0.13f, 0.13f, 0.13f), armour ? steel : skin);
        }
        void Gun(float x, float len) { b.Xf = new Transform3D(new Basis(Vector3.Back, -0.5f), V(x, g + 0.42f, 0.1f)); b.Box(V(0, 0, 0), V(len, 0.035f, 0.035f), dark); b.Box(V(-len * 0.35f, -0.01f, 0), V(len * 0.3f, 0.06f, 0.04f), wood); b.Xf = Transform3D.Identity; }
        void Hull(float len, float wide, float tall, Color col)
        {
            b.Box(V(0, g + tall / 2, 0), V(len, tall, wide), col);
            b.Tri(V(len / 2, g, -wide / 2), V(len / 2, g, wide / 2), V(len / 2 + 0.16f, g + tall, 0), Vector3.Down, col.Darkened(0.1f));
            b.Tri(V(len / 2, g + tall, -wide / 2), V(len / 2, g + tall, wide / 2), V(len / 2 + 0.16f, g + tall, 0), Vector3.Up, col.Lightened(0.05f));
            b.Tri(V(len / 2, g, wide / 2), V(len / 2, g + tall, wide / 2), V(len / 2 + 0.16f, g + tall, 0), Vector3.Back, col);
            b.Tri(V(len / 2, g, -wide / 2), V(len / 2, g + tall, -wide / 2), V(len / 2 + 0.16f, g + tall, 0), Vector3.Forward, col.Darkened(0.15f));
        }
        void Sail(float x, float tall, float wide)
        {
            Pole(x, 0, g + 0.1f, g + 0.16f + tall, wood);
            b.Box(V(x + 0.02f, g + 0.2f + tall / 2 - 0.03f, 0), V(0.03f, tall * 0.8f, wide), cloth);
            b.Box(V(x + 0.04f, g + 0.2f + tall / 2 - 0.03f, 0), V(0.012f, tall * 0.28f, wide * 0.7f), team);
        }
        void Flag(float x, float top)
        {
            Pole(x, 0, g, top, wood, 0.025f);
            b.Box(V(x + 0.08f, top - 0.06f, 0), V(0.16f, 0.1f, 0.02f), team.Lightened(0.15f));
        }
        void Turret(float x, float y, float size, Color col)
        {
            b.Box(V(x, y, 0), V(size, size * 0.5f, size * 0.8f), col);
            b.Box(V(x + size * 0.9f, y + size * 0.05f, 0), V(size * 1.1f, size * 0.16f, size * 0.16f), dark);
        }

        switch (def.Id)
        {
            case "settler":
                // a covered wagon
                b.Box(V(0, g + 0.17f, 0), V(0.5f, 0.1f, 0.26f), wood);
                b.Box(V(0, g + 0.33f, 0), V(0.42f, 0.22f, 0.26f), cloth);
                b.Box(V(0, g + 0.46f, 0), V(0.34f, 0.06f, 0.2f), cloth.Darkened(0.06f));
                foreach (int sx in new[] { -1, 1 })
                    foreach (int sz in new[] { -1, 1 })
                    {
                        b.Xf = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), V(sx * 0.16f, g + 0.1f, sz * 0.14f));
                        b.Column(V(0, -0.015f, 0), 0.1f, 0.03f, dark, 8);
                        b.Xf = Transform3D.Identity;
                    }
                Flag(-0.2f, g + 0.72f);
                break;
            case "worker":
                Man();
                b.Box(V(0, g + 0.7f, 0), V(0.2f, 0.05f, 0.2f), gold);          // a builder's hat
                Pole(0.2f, 0.06f, g + 0.12f, g + 0.62f, wood);
                b.Box(V(0.2f, g + 0.62f, 0.06f), V(0.24f, 0.05f, 0.05f), steel);   // pickaxe
                break;
            case "scout":
                Man(0, 0.9f, C(0x4E7A3A));
                b.Box(V(0, g + 0.36f, -0.08f), V(0.26f, 0.34f, 0.04f), team);        // cloak
                b.Pyramid(V(0, g + 0.61f, 0), 0.12f, 0.12f, 4, C(0x4E7A3A), Mathf.Pi / 4);
                Pole(0.18f, 0.05f, g, g + 0.66f, wood, 0.025f);
                break;
            case "warrior":
                Man();
                Pole(0.19f, 0.06f, g + 0.25f, g + 0.56f, wood, 0.04f);
                b.Box(V(0.19f, g + 0.6f, 0.06f), V(0.09f, 0.12f, 0.09f), wood.Darkened(0.2f));  // club
                Shield(-0.16f, true);
                break;
            case "archer":
                Man(); Bow(0.2f, 0.2f);
                b.Box(V(-0.06f, g + 0.42f, -0.1f), V(0.07f, 0.22f, 0.05f), wood);     // quiver
                break;
            case "longbowman":
                Man(); Bow(0.2f, 0.3f);
                b.Pyramid(V(0, g + 0.68f, 0), 0.13f, 0.1f, 4, C(0x3E6A3A), Mathf.Pi / 4);
                b.Box(V(-0.06f, g + 0.42f, -0.1f), V(0.07f, 0.22f, 0.05f), wood);
                break;
            case "spearman":
                Man(); Helmet(0, 1, C(0xB98A4A));
                Pole(0.2f, 0.06f, g, g + 0.92f, wood, 0.025f);
                b.Pyramid(V(0.2f, g + 0.92f, 0.06f), 0.04f, 0.12f, 4, steel);
                Shield(-0.16f, true);
                break;
            case "swordsman":
                Man(0, 1, null, steel.Darkened(0.3f)); Helmet(0, 1, steel, true);
                Pole(0.2f, 0.07f, g + 0.34f, g + 0.78f, steel, 0.035f);
                b.Box(V(0.2f, g + 0.36f, 0.07f), V(0.1f, 0.03f, 0.04f), gold);
                Shield(-0.16f, false);
                break;
            case "horseman":
                Horse(C(0x7A5232), false);
                Pole(0.1f, 0.1f, g + 0.4f, g + 0.98f, wood, 0.022f);
                b.Pyramid(V(0.1f, g + 0.98f, 0.1f), 0.035f, 0.1f, 4, steel);
                break;
            case "knight":
                Horse(C(0xD8D4CC), true);
                b.Box(V(-0.03f, g + 0.85f, 0), V(0.04f, 0.07f, 0.14f), team.Lightened(0.2f));
                b.Xf = new Transform3D(new Basis(Vector3.Back, -1.25f), V(0.14f, g + 0.6f, 0.11f));
                b.Column(V(0, -0.3f, 0), 0.025f, 0.75f, cloth, 5, 0.008f);
                b.Xf = Transform3D.Identity;
                break;
            case "cavalry":
                Horse(C(0x3A2A20), false);
                b.Box(V(-0.03f, g + 0.85f, 0), V(0.17f, 0.05f, 0.17f), dark);
                b.Xf = new Transform3D(new Basis(Vector3.Back, -0.6f), V(0.12f, g + 0.72f, 0.1f));
                b.Box(V(0, 0.1f, 0), V(0.03f, 0.34f, 0.02f), steel);
                b.Xf = Transform3D.Identity;
                break;
            case "musketman":
                Man(); Gun(0.14f, 0.5f);
                b.Box(V(0, g + 0.7f, 0), V(0.26f, 0.035f, 0.26f), dark);
                b.Box(V(0, g + 0.73f, 0), V(0.14f, 0.05f, 0.14f), dark);
                break;
            case "rifleman":
                Man(0, 1, null, C(0x4A4A3A)); Gun(0.14f, 0.56f);
                b.Box(V(0, g + 0.71f, 0), V(0.16f, 0.06f, 0.16f), team.Darkened(0.2f));
                b.Box(V(0, g + 0.69f, 0.09f), V(0.14f, 0.02f, 0.06f), dark);
                b.Box(V(0, g + 0.4f, 0.085f), V(0.05f, 0.3f, 0.01f), cloth);          // cross-belt
                break;
            case "infantry":
                Man(0, 1, C(0x5A6A3A).Lerp(team, 0.45f), C(0x3A4428)); Gun(0.14f, 0.5f);
                b.Column(V(0, g + 0.67f, 0), 0.11f, 0.06f, C(0x4A5A32), 8, 0.08f);
                b.Box(V(0, g + 0.38f, -0.11f), V(0.18f, 0.2f, 0.07f), C(0x4A5A32));   // backpack
                break;
            case "tank":
                foreach (int s in new[] { -1, 1 }) b.Box(V(0, g + 0.07f, s * 0.19f), V(0.64f, 0.14f, 0.1f), dark);
                b.Box(V(0, g + 0.18f, 0), V(0.58f, 0.14f, 0.34f), C(0x5A6A3A));
                b.Box(V(-0.04f, g + 0.31f, 0), V(0.28f, 0.13f, 0.24f), team.Darkened(0.15f));
                b.Box(V(0.3f, g + 0.31f, 0), V(0.44f, 0.05f, 0.05f), dark);
                break;
            case "mech_infantry":
                b.Box(V(0, g + 0.2f, 0), V(0.6f, 0.2f, 0.3f), C(0x6A745A));
                b.Box(V(-0.06f, g + 0.34f, 0), V(0.4f, 0.1f, 0.26f), team.Darkened(0.15f));
                b.Tri(V(0.3f, g + 0.1f, -0.15f), V(0.3f, g + 0.3f, -0.15f), V(0.4f, g + 0.1f, -0.15f), Vector3.Forward, C(0x6A745A));
                b.Tri(V(0.3f, g + 0.1f, 0.15f), V(0.3f, g + 0.3f, 0.15f), V(0.4f, g + 0.1f, 0.15f), Vector3.Back, C(0x6A745A));
                b.Quad(V(0.3f, g + 0.3f, -0.15f), V(0.3f, g + 0.3f, 0.15f), V(0.4f, g + 0.1f, 0.15f), V(0.4f, g + 0.1f, -0.15f), Vector3.Right + Vector3.Up, C(0x7A846A));
                foreach (int sx in new[] { -1, 0, 1 })
                    foreach (int sz in new[] { -1, 1 })
                    {
                        b.Xf = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), V(sx * 0.2f, g + 0.08f, sz * 0.16f));
                        b.Column(V(0, -0.02f, 0), 0.08f, 0.04f, dark, 8);
                        b.Xf = Transform3D.Identity;
                    }
                Turret(0, g + 0.43f, 0.12f, C(0x4A5440));
                break;
            case "nano_trooper":
                Man(0, 1.08f, C(0x20242E), C(0x20242E), C(0x20242E));
                b.Box(V(0, g + 0.66f, 0.085f), V(0.13f, 0.04f, 0.02f), C(0x5FF0FF));      // visor
                b.Box(V(0, g + 0.42f, 0.09f), V(0.1f, 0.1f, 0.02f), team.Lightened(0.3f));
                foreach (int s in new[] { -1, 1 }) b.Box(V(s * 0.17f, g + 0.5f, 0), V(0.07f, 0.1f, 0.14f), team);
                b.Box(V(0.2f, g + 0.36f, 0.1f), V(0.06f, 0.06f, 0.3f), C(0x5FF0FF));
                break;
            case "missionary":
                b.Column(V(0, g, 0), 0.17f, 0.5f, cloth, 8, 0.1f);                        // robe
                b.Box(V(0, g + 0.3f, 0.12f), V(0.07f, 0.34f, 0.02f), team);
                b.Box(V(0, g + 0.58f, 0), V(0.15f, 0.15f, 0.15f), skin);
                Pole(0.2f, 0.04f, g, g + 0.86f, wood, 0.025f);
                b.Gem(V(0.2f, g + 0.92f, 0.04f), 0.07f, gold, 1, 4);
                break;
            case "work_boat":
                Hull(0.44f, 0.22f, 0.12f, wood);
                Pole(-0.05f, 0, g + 0.1f, g + 0.42f, wood, 0.025f);
                b.Tri(V(-0.05f, g + 0.42f, 0), V(-0.05f, g + 0.16f, 0), V(0.2f, g + 0.16f, 0), Vector3.Back, team);
                b.Tri(V(-0.05f, g + 0.42f, 0), V(-0.05f, g + 0.16f, 0), V(0.2f, g + 0.16f, 0), Vector3.Forward, team);
                b.Box(V(-0.16f, g + 0.15f, 0), V(0.1f, 0.06f, 0.14f), C(0x8FC4E8));        // the catch
                break;
            case "galley":
                Hull(0.6f, 0.22f, 0.12f, wood);
                Sail(0, 0.34f, 0.3f);
                foreach (int k in new[] { -2, -1, 0, 1, 2 })
                    foreach (int s in new[] { -1, 1 })
                        b.Box(V(k * 0.1f, g + 0.07f, s * 0.19f), V(0.02f, 0.02f, 0.16f), cloth.Darkened(0.2f));
                break;
            case "caravel":
                Hull(0.6f, 0.24f, 0.16f, wood);
                b.Box(V(-0.24f, g + 0.21f, 0), V(0.14f, 0.1f, 0.24f), wood.Darkened(0.15f));
                Sail(0.1f, 0.4f, 0.3f); Sail(-0.14f, 0.3f, 0.24f);
                break;
            case "frigate":
                Hull(0.7f, 0.25f, 0.17f, wood.Darkened(0.1f));
                b.Box(V(0, g + 0.1f, 0), V(0.7f, 0.04f, 0.26f), gold.Darkened(0.2f));        // gun deck stripe
                Sail(0.2f, 0.42f, 0.3f); Sail(-0.02f, 0.5f, 0.32f); Sail(-0.24f, 0.38f, 0.28f);
                break;
            case "ironclad":
                Hull(0.66f, 0.26f, 0.12f, C(0x4A4E56));
                b.Box(V(0, g + 0.18f, 0), V(0.4f, 0.12f, 0.22f), C(0x5A5F6A));
                b.Column(V(-0.02f, g + 0.24f, 0), 0.05f, 0.24f, dark, 6);
                b.Gem(V(-0.06f, g + 0.56f, 0), 0.06f, C(0x8A8A90));                        // smoke
                Flag(-0.28f, g + 0.5f);
                break;
            case "destroyer":
                Hull(0.74f, 0.18f, 0.12f, C(0x8A939E));
                b.Box(V(-0.04f, g + 0.19f, 0), V(0.24f, 0.14f, 0.14f), C(0xA6AEB8));
                b.Column(V(-0.12f, g + 0.26f, 0), 0.035f, 0.14f, dark, 6);
                Turret(0.2f, g + 0.15f, 0.09f, C(0x6A727C));
                b.Box(V(0, g + 0.02f, 0), V(0.74f, 0.03f, 0.19f), team);
                break;
            case "battleship":
                Hull(0.8f, 0.3f, 0.15f, C(0x7A838E));
                b.Box(V(-0.04f, g + 0.24f, 0), V(0.24f, 0.18f, 0.2f), C(0x98A0AA));
                b.Box(V(-0.04f, g + 0.4f, 0), V(0.1f, 0.14f, 0.1f), C(0x98A0AA));
                b.Column(V(-0.18f, g + 0.3f, 0), 0.045f, 0.16f, dark, 6);
                Turret(0.2f, g + 0.2f, 0.13f, C(0x5A626C)); Turret(0.33f, g + 0.17f, 0.1f, C(0x5A626C));
                b.Box(V(0, g + 0.02f, 0), V(0.8f, 0.03f, 0.31f), team);
                break;
            case "missile_cruiser":
                Hull(0.8f, 0.24f, 0.13f, C(0x9AA4B0));
                b.Box(V(-0.08f, g + 0.22f, 0), V(0.26f, 0.18f, 0.18f), C(0xB4BCC6));
                b.Pyramid(V(-0.08f, g + 0.31f, 0), 0.06f, 0.26f, 4, C(0x7A838E));          // radar mast
                foreach (int k in new[] { 0, 1 })
                {
                    b.Xf = new Transform3D(new Basis(Vector3.Back, -0.7f), V(0.14f + k * 0.14f, g + 0.2f, 0));
                    b.Box(V(0, 0, 0), V(0.06f, 0.22f, 0.14f), C(0xEDEFF2));
                    b.Box(V(0, 0.12f, 0), V(0.06f, 0.03f, 0.14f), C(0xD0302A));
                    b.Xf = Transform3D.Identity;
                }
                b.Box(V(0, g + 0.02f, 0), V(0.8f, 0.03f, 0.25f), team);
                break;
            case "shaman":
                Man(0, 1, C(0x7A5A34));
                b.Box(V(0, g + 0.36f, -0.085f), V(0.28f, 0.32f, 0.03f), team);
                foreach (int s in new[] { -1, 1 })                                           // antlers
                {
                    b.Box(V(s * 0.07f, g + 0.76f, 0), V(0.025f, 0.16f, 0.025f), cloth);
                    b.Box(V(s * 0.12f, g + 0.8f, 0), V(0.09f, 0.025f, 0.025f), cloth);
                }
                Pole(0.2f, 0.05f, g, g + 0.8f, wood, 0.03f);
                b.Gem(V(0.2f, g + 0.87f, 0.05f), 0.07f, C(0x7FE0A8));
                break;
            case "spirit":
                b.Pyramid(V(0, g + 0.36f, 0), 0.2f, -0.3f, 7, C(0xBFF4E8));              // a wisp with a trailing tail
                b.Pyramid(V(0, g + 0.36f, 0), 0.2f, 0.26f, 7, C(0xE6FFF8));
                b.Box(V(-0.07f, g + 0.44f, 0.15f), V(0.04f, 0.06f, 0.02f), team.Darkened(0.2f));
                b.Box(V(0.07f, g + 0.44f, 0.15f), V(0.04f, 0.06f, 0.02f), team.Darkened(0.2f));
                break;
            case "werebeast":
                b.Box(V(-0.02f, g + 0.3f, 0), V(0.4f, 0.26f, 0.24f), C(0x4A3A30));        // hunched body
                b.Box(V(0.2f, g + 0.46f, 0), V(0.2f, 0.18f, 0.2f), C(0x5A483A));
                b.Box(V(0.32f, g + 0.42f, 0), V(0.1f, 0.08f, 0.1f), C(0x3A2C24));         // muzzle
                foreach (int s in new[] { -1, 1 })
                {
                    b.Pyramid(V(0.18f, g + 0.55f, s * 0.07f), 0.04f, 0.1f, 4, C(0x3A2C24));
                    b.Box(V(0.12f, g + 0.09f, s * 0.09f), V(0.07f, 0.18f, 0.07f), C(0x3A2C24));
                    b.Box(V(-0.14f, g + 0.09f, s * 0.09f), V(0.07f, 0.18f, 0.07f), C(0x3A2C24));
                }
                b.Box(V(-0.02f, g + 0.44f, 0), V(0.3f, 0.03f, 0.1f), team);               // mane in the team colour
                break;
            case "familiar":
                b.Gem(V(0, g + 0.3f, 0), 0.12f, C(0x6A4AA8), 1.1f);
                foreach (int s in new[] { -1, 1 })
                {
                    b.Tri(V(0, g + 0.34f, s * 0.06f), V(-0.1f, g + 0.52f, s * 0.34f), V(0.1f, g + 0.3f, s * 0.3f), Vector3.Up, team.Lightened(0.2f));
                    b.Tri(V(0, g + 0.34f, s * 0.06f), V(-0.1f, g + 0.52f, s * 0.34f), V(0.1f, g + 0.3f, s * 0.3f), Vector3.Down, team);
                    b.Box(V(0.09f, g + 0.36f, s * 0.05f), V(0.03f, 0.03f, 0.03f), gold);
                }
                break;
            case "battle_mage":
                b.Column(V(0, g, 0), 0.18f, 0.5f, C(0x4A3A8A), 8, 0.1f);
                b.Box(V(0, g + 0.3f, 0.13f), V(0.07f, 0.34f, 0.02f), team);
                b.Box(V(0, g + 0.58f, 0), V(0.15f, 0.15f, 0.15f), skin);
                b.Column(V(0, g + 0.65f, 0), 0.16f, 0.02f, C(0x3A2C72), 8);
                b.Pyramid(V(0, g + 0.67f, 0), 0.1f, 0.26f, 6, C(0x3A2C72));               // pointed hat
                Pole(0.22f, 0.05f, g, g + 0.78f, wood, 0.03f);
                b.Gem(V(0.22f, g + 0.86f, 0.05f), 0.08f, C(0xFF8A3A));
                break;
            case "golem":
                foreach (int s in new[] { -1, 1 })
                {
                    b.Box(V(s * 0.1f, g + 0.12f, 0), V(0.14f, 0.24f, 0.18f), C(0x8A8A84));
                    b.Box(V(s * 0.27f, g + 0.36f, 0), V(0.13f, 0.4f, 0.16f), C(0x9A9A94));
                }
                b.Box(V(0, g + 0.44f, 0), V(0.4f, 0.4f, 0.26f), C(0xA6A6A0));
                b.Box(V(0, g + 0.73f, 0), V(0.2f, 0.16f, 0.2f), C(0x8A8A84));
                b.Box(V(0, g + 0.74f, 0.105f), V(0.13f, 0.035f, 0.01f), C(0x5FE0FF));     // glowing eyes
                b.Box(V(0, g + 0.46f, 0.135f), V(0.12f, 0.12f, 0.01f), team.Lightened(0.2f)); // rune
                break;
            default:
                // units from mods: recognised by what they can do
                if (def.Sea) { Hull(0.6f, 0.24f, 0.14f, wood); Sail(0, 0.4f, 0.3f); }
                else if (def.X.Construct) goto case "golem";
                else if (def.X.Flying) goto case "spirit";
                else if (def.X.Ranged > 0) goto case "battle_mage";
                else if (def.X.HealAura > 0) goto case "shaman";
                else if (def.Role == UnitRole.Settler) goto case "settler";
                else if (def.Role == UnitRole.Worker) goto case "worker";
                else if (def.Role == UnitRole.Missionary) goto case "missionary";
                else if (def.Moves >= 2 && def.Str >= 5) goto case "horseman";
                else { Man(); Helmet(0, 1, steel); Pole(0.2f, 0.07f, g + 0.34f, g + 0.78f, steel, 0.035f); Shield(-0.16f, false); }
                break;
        }
    }
}

/// <summary>Cities and tile improvements: every building of a city stands in it as its own little landmark.</summary>
public static class Town
{
    private static Color C(int rgb) => Ui.Col(rgb);
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private static int Hash(string s)
    {
        unchecked { uint h = 2166136261; foreach (char ch in s) h = (h ^ ch) * 16777619; return (int)(h & 0x7FFFFFFF); }
    }

    /// <summary>
    /// The whole city as one mesh: houses that multiply with the population and change with the era, a landmark per
    /// building on a spot that never moves once taken, walls around it all, and the owner's flag.
    /// </summary>
    public static Mesh City(int cityId, int pop, int era, bool capital, IReadOnlyCollection<string> buildings, Color team)
    {
        var b = new MeshBuilder { ContactShade = 0.19f };
        // Warm paving and open ground leave a small parade square in the centre.
        b.Column(V(0, 0.002f, 0), 0.4f, 0.009f, C(0x92866A), 10);
        b.Column(V(0, 0.011f, 0), 0.373f, 0.005f, C(0xC5B58D), 10);
        // Two rings of plots around the garrison square.
        var slots = new List<Vector3>();
        for (int i = 0; i < 8; i++) { float a = Mathf.Tau * i / 8 + 0.2f; slots.Add(V(Mathf.Cos(a) * 0.53f, 0, Mathf.Sin(a) * 0.53f)); }
        for (int i = 0; i < 14; i++) { float a = Mathf.Tau * i / 14; slots.Add(V(Mathf.Cos(a) * 0.74f, 0, Mathf.Sin(a) * 0.74f)); }
        var taken = new bool[slots.Count];

        void At(Vector3 p, float scale, Action draw)
        {
            // every landmark faces the camera's usual side
            b.Xf = new Transform3D(Basis.Identity.Scaled(Vector3.One * scale), p);
            draw();
            b.Xf = Transform3D.Identity;
        }

        bool walls = buildings.Contains("walls");
        foreach (string id in buildings.OrderBy(x => x != "palace").ThenBy(x => x, StringComparer.Ordinal))
        {
            if (id == "walls") continue;
            int start = id == "palace" ? 11 + 8 : Hash(id) % slots.Count, slot = -1; // the palace: back of the outer ring
            for (int k = 0; k < slots.Count && slot < 0; k++) if (!taken[(start + k) % slots.Count]) slot = (start + k) % slots.Count;
            if (slot < 0) break;
            taken[slot] = true;
            string what = id;
            At(slots[slot], id == "palace" ? 1.35f : 1.12f, () => Landmark(b, what, team));
        }

        // houses on the plots that are left: more of them as the city grows
        var rnd = new Random(cityId * 7919 + 13);
        int houses = Math.Clamp(2 + pop, 3, slots.Count);
        var order = Enumerable.Range(0, slots.Count).OrderBy(_ => rnd.Next()).ToList();
        foreach (int i in order)
        {
            if (houses <= 0) break;
            if (taken[i]) continue;
            houses--;
            float tall = (float)rnd.NextDouble();
            bool teamRoof = rnd.Next(4) == 0;
            At(slots[i] + V((float)rnd.NextDouble() * 0.06f - 0.03f, 0, (float)rnd.NextDouble() * 0.06f - 0.03f), 1.2f, () => House(b, era, tall, teamRoof ? team : (Color?)null, pop));
        }

        if (walls) Walls(b, team);
        // the flag: taller and with a gold top over a capital
        b.Box(V(0.6f, 0.6f, -0.62f), V(0.035f, 1.2f, 0.035f), C(0x6B4A2C));
        b.Box(V(0.74f, 1.08f, -0.62f), V(0.26f, 0.17f, 0.02f), team);
        if (capital) b.Gem(V(0.6f, 1.25f, -0.62f), 0.06f, C(0xF2CF63), 1, 4);
        return b.Commit()!;
    }

    private static void House(MeshBuilder b, int era, float tall, Color? roof, int pop)
    {
        if (era <= 0)
        {   // huts
            float wall = 0.13f + tall * 0.045f;
            b.Column(V(0, 0, 0), 0.106f, 0.032f, C(0x9B8C79), 6);
            b.Column(V(0, 0.031f, 0), 0.085f, wall, C(0xDFCAA7), 6);
            b.Box(V(0, 0.048f, 0.08f), V(0.045f, 0.088f, 0.027f), C(0x857063));
            b.Pyramid(V(0, wall + 0.032f, 0), 0.13f, 0.14f, 6, roof ?? (tall > 0.5f ? C(0xAA7357) : C(0xB69965)));
        }
        else if (era <= 3)
        {   // plastered houses under tiled roofs
            float h = 0.13f + tall * 0.1f + Math.Min(0.08f, pop * 0.006f);
            b.Box(V(0, h / 2, 0), V(0.17f, h, 0.15f), C(0xE7D7BE));
            b.Box(V(0, 0.047f, 0.078f), V(0.044f, 0.09f, 0.018f), C(0x6D5142));
            b.Pyramid(V(0, h, 0), 0.14f, 0.1f, 4, roof ?? (tall > 0.5f ? C(0xB3755D) : C(0x945F54)), Mathf.Pi / 4);
            if (tall > 0.66f) b.Box(V(-0.037f, h + 0.055f, 0.015f), V(0.035f, 0.11f, 0.037f), C(0xA68068));
        }
        else if (era <= 5)
        {   // brick blocks with flat roofs
            float h = 0.2f + tall * 0.22f;
            b.Box(V(0, h / 2, 0), V(0.17f, h, 0.16f), tall > 0.5f ? C(0xA8705A) : C(0xB8B0A4));
            b.Box(V(0, h + 0.012f, 0), V(0.19f, 0.024f, 0.18f), roof ?? C(0x55504A));
            for (float y = 0.08f; y < h - 0.04f; y += 0.08f) b.Box(V(0, y, 0.081f), V(0.12f, 0.03f, 0.005f), C(0x2E3A4A));
        }
        else
        {   // glass towers
            float h = 0.3f + tall * 0.45f;
            b.Box(V(0, h / 2, 0), V(0.15f, h, 0.15f), tall > 0.5f ? C(0x7FB6D8) : C(0x9AC8E0));
            b.Box(V(0, h + 0.015f, 0), V(0.1f, 0.03f, 0.1f), roof ?? C(0xDCE6EE));
            for (float y = 0.1f; y < h; y += 0.1f) b.Box(V(0, y, 0), V(0.155f, 0.012f, 0.155f), C(0x4A6A88));
        }
    }

    private static void Walls(MeshBuilder b, Color team)
    {
        var stone = C(0xB4AEA0);
        const float r = 0.97f;
        for (int i = 0; i < 6; i++)
        {
            float a0 = Mathf.Tau * i / 6 + Mathf.Pi / 6, a1 = a0 + Mathf.Tau / 6;
            var p0 = V(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r); var p1 = V(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
            var mid = (p0 + p1) / 2;
            float len = p0.DistanceTo(p1), ang = Mathf.Atan2(-(p1.Z - p0.Z), p1.X - p0.X);
            b.Xf = new Transform3D(new Basis(Vector3.Up, ang), mid);
            bool gate = i == 0; // the edge facing the camera
            if (gate)
            {
                b.Box(V(-len * 0.32f, 0.11f, 0), V(len * 0.36f, 0.22f, 0.07f), stone);
                b.Box(V(len * 0.32f, 0.11f, 0), V(len * 0.36f, 0.22f, 0.07f), stone);
                b.Box(V(0, 0.25f, 0), V(len * 0.3f, 0.1f, 0.09f), stone.Darkened(0.1f));
                b.Box(V(0, 0.1f, 0), V(len * 0.26f, 0.2f, 0.03f), C(0x5A3E26));
            }
            else b.Box(V(0, 0.11f, 0), V(len, 0.22f, 0.07f), stone);
            for (float x = -len / 2 + 0.1f; x < len / 2 - 0.05f; x += 0.16f)
                if (!gate || Math.Abs(x) > len * 0.16f) b.Box(V(x, 0.245f, 0), V(0.07f, 0.05f, 0.08f), stone.Lightened(0.08f));
            b.Xf = Transform3D.Identity;
            b.Column(p0, 0.085f, 0.34f, stone.Darkened(0.06f), 6);
            b.Pyramid(p0 + V(0, 0.34f, 0), 0.1f, 0.1f, 6, team);
        }
    }

    /// <summary>One building, about a fifth of a tile wide, standing at the origin.</summary>
    private static void Landmark(MeshBuilder b, string id, Color team)
    {
        Color white = C(0xF2EEE4), stone = C(0xB8B2A6), brick = C(0xA8644A), dark = C(0x2E2E34), gold = C(0xF2CF63), wood = C(0x7A5232),
            blue = C(0x3E6FB8), glass = C(0x7FD0F0);
        void Columns(float w, float h, float z)
        {
            for (int i = -1; i <= 1; i++) b.Box(V(i * w * 0.36f, h / 2, z), V(0.03f, h, 0.03f), white);
        }
        void Hall(float w, float h, float d, Color wall, Color roof, bool pitched = true)
        {
            b.Box(V(0, h / 2, 0), V(w, h, d), wall);
            if (pitched)
            {
                b.Quad(V(-w / 2 - 0.01f, h, d / 2 + 0.01f), V(w / 2 + 0.01f, h, d / 2 + 0.01f), V(w / 2 + 0.01f, h + w * 0.3f, 0), V(-w / 2 - 0.01f, h + w * 0.3f, 0), Vector3.Back + Vector3.Up, roof);
                b.Quad(V(-w / 2 - 0.01f, h, -d / 2 - 0.01f), V(w / 2 + 0.01f, h, -d / 2 - 0.01f), V(w / 2 + 0.01f, h + w * 0.3f, 0), V(-w / 2 - 0.01f, h + w * 0.3f, 0), Vector3.Forward + Vector3.Up, roof.Darkened(0.12f));
                foreach (int s in new[] { -1, 1 })
                    b.Tri(V(s * (w / 2), h, d / 2), V(s * (w / 2), h, -d / 2), V(s * (w / 2), h + w * 0.3f, 0), V(s, 0, 0), wall);
            }
            else b.Box(V(0, h + 0.012f, 0), V(w + 0.02f, 0.024f, d + 0.02f), roof);
        }
        void Chimney(float x, float z, float h, bool smoke = true)
        {
            b.Column(V(x, 0, z), 0.03f, h, C(0x6A3A2E), 6, 0.024f);
            if (smoke) { b.Gem(V(x + 0.02f, h + 0.05f, z), 0.04f, C(0xCFCFD4)); b.Gem(V(x + 0.05f, h + 0.13f, z), 0.03f, C(0xE4E4E8)); }
        }

        switch (id)
        {
            case "palace":
                Hall(0.32f, 0.2f, 0.2f, white, team, false);
                b.Box(V(0, 0.27f, 0), V(0.16f, 0.12f, 0.14f), white);
                b.Gem(V(0, 0.37f, 0), 0.08f, gold, 0.9f, 8);
                foreach (int s in new[] { -1, 1 }) { b.Column(V(s * 0.17f, 0, 0.06f), 0.04f, 0.32f, white, 6); b.Pyramid(V(s * 0.17f, 0.32f, 0.06f), 0.055f, 0.1f, 6, team); }
                b.Box(V(0, 0.07f, 0.101f), V(0.06f, 0.14f, 0.01f), team.Darkened(0.2f));
                break;
            case "shrine":
                b.Box(V(0, 0.03f, 0), V(0.18f, 0.06f, 0.18f), stone);
                b.Box(V(0, 0.1f, 0), V(0.1f, 0.08f, 0.1f), stone.Lightened(0.1f));
                b.Gem(V(0, 0.21f, 0), 0.05f, C(0xFFD76A), 1.6f, 5);
                break;
            case "monument":
                b.Box(V(0, 0.025f, 0), V(0.16f, 0.05f, 0.16f), stone);
                b.Column(V(0, 0.05f, 0), 0.045f, 0.36f, white, 4, 0.025f);
                b.Pyramid(V(0, 0.41f, 0), 0.03f, 0.06f, 4, gold);
                break;
            case "granary":
                b.Column(V(0, 0, 0), 0.1f, 0.2f, C(0xD8C08A), 8);
                b.Pyramid(V(0, 0.2f, 0), 0.13f, 0.12f, 8, C(0xB8923E));
                b.Box(V(0, 0.06f, 0.1f), V(0.05f, 0.1f, 0.01f), wood);
                break;
            case "library":
                Hall(0.24f, 0.16f, 0.16f, white, blue);
                Columns(0.24f, 0.16f, 0.095f);
                break;
            case "temple":
                b.Box(V(0, 0.02f, 0), V(0.28f, 0.04f, 0.22f), stone);
                Hall(0.2f, 0.2f, 0.14f, white, gold.Darkened(0.1f));
                foreach (int z in new[] { -1, 1 }) Columns(0.26f, 0.2f, z * 0.09f);
                break;
            case "harbor":
                b.Box(V(0, 0.02f, 0), V(0.3f, 0.04f, 0.1f), wood);
                b.Box(V(0.1f, 0.02f, 0.1f), V(0.08f, 0.04f, 0.16f), wood);
                b.Box(V(-0.07f, 0.09f, 0), V(0.12f, 0.1f, 0.09f), C(0xD8C8A8));
                b.Box(V(0.1f, 0.16f, 0), V(0.02f, 0.26f, 0.02f), dark);
                b.Box(V(0.1f, 0.28f, 0.06f), V(0.02f, 0.02f, 0.16f), dark);
                b.Box(V(-0.02f, 0.05f, 0.13f), V(0.1f, 0.03f, 0.05f), C(0x8FC4E8));
                break;
            case "market":
                foreach (var (x, col) in new[] { (-0.09f, C(0xD0402E)), (0.0f, C(0xF2C230)), (0.09f, C(0x3E8FD0)) })
                {
                    b.Box(V(x, 0.04f, 0), V(0.075f, 0.08f, 0.1f), wood);
                    b.Quad(V(x - 0.045f, 0.16f, -0.06f), V(x + 0.045f, 0.16f, -0.06f), V(x + 0.045f, 0.11f, 0.09f), V(x - 0.045f, 0.11f, 0.09f), Vector3.Up, col);
                    b.Box(V(x, 0.1f, -0.055f), V(0.012f, 0.12f, 0.012f), wood);
                }
                break;
            case "forge":
                Hall(0.2f, 0.13f, 0.16f, C(0x4A4A50), C(0x2E2E34));
                b.Box(V(0, 0.05f, 0.081f), V(0.08f, 0.08f, 0.01f), C(0xFF7A1F));
                Chimney(0.06f, -0.04f, 0.32f);
                break;
            case "courthouse":
                Hall(0.24f, 0.17f, 0.17f, white, stone, false);
                b.Gem(V(0, 0.22f, 0), 0.08f, stone.Lightened(0.1f), 0.7f, 8);
                Columns(0.24f, 0.17f, 0.1f);
                break;
            case "colosseum":
                b.Column(V(0, 0, 0), 0.15f, 0.14f, C(0xD8CBB0), 10, 0.16f);
                b.Column(V(0, 0.1f, 0), 0.11f, 0.045f, C(0x8A7A5A), 10);
                b.Column(V(0, 0.105f, 0), 0.07f, 0.045f, C(0xD9C47A), 10);
                break;
            case "university":
                Hall(0.26f, 0.16f, 0.15f, C(0xC8A078), blue);
                b.Box(V(0.09f, 0.16f, 0), V(0.08f, 0.32f, 0.08f), C(0xC8A078));
                b.Pyramid(V(0.09f, 0.32f, 0), 0.07f, 0.12f, 4, blue, Mathf.Pi / 4);
                break;
            case "bank":
                Hall(0.2f, 0.2f, 0.16f, C(0xC8CCB8), C(0x6A7A5A), false);
                Columns(0.2f, 0.2f, 0.09f);
                b.Box(V(0, 0.26f, 0), V(0.07f, 0.07f, 0.07f), gold);
                break;
            case "observatory":
                b.Column(V(0, 0, 0), 0.09f, 0.18f, white, 8);
                b.Pyramid(V(0, 0.18f, 0), 0.1f, 0.1f, 8, C(0x8FA8C8));
                b.Xf *= new Transform3D(new Basis(Vector3.Back, -0.8f), V(0.02f, 0.25f, 0));
                b.Box(V(0, 0.05f, 0), V(0.03f, 0.16f, 0.03f), dark);
                break;
            case "theatre":
                Hall(0.24f, 0.17f, 0.16f, white, C(0x8A3A5A));
                b.Box(V(0, 0.08f, 0.081f), V(0.12f, 0.13f, 0.01f), C(0xC82A3A));
                break;
            case "factory":
                b.Box(V(0, 0.08f, 0), V(0.28f, 0.16f, 0.18f), brick);
                for (int i = -1; i <= 1; i++)
                    b.Quad(V(i * 0.093f - 0.046f, 0.16f, 0.09f), V(i * 0.093f - 0.046f, 0.16f, -0.09f), V(i * 0.093f + 0.046f, 0.22f, -0.09f), V(i * 0.093f + 0.046f, 0.22f, 0.09f), Vector3.Up + Vector3.Left, C(0x55504A));
                Chimney(-0.1f, -0.05f, 0.4f); Chimney(0.0f, -0.05f, 0.34f);
                break;
            case "power_plant":
                b.Column(V(-0.05f, 0, 0), 0.1f, 0.26f, C(0xD0D0CC), 10, 0.07f);
                b.Gem(V(-0.05f, 0.33f, 0), 0.06f, C(0xF4F4F6));
                b.Box(V(0.1f, 0.06f, 0), V(0.1f, 0.12f, 0.14f), C(0x8A8A90));
                b.Box(V(0.1f, 0.14f, 0), V(0.02f, 0.06f, 0.02f), gold);
                break;
            case "broadcast":
                b.Box(V(0, 0.03f, 0), V(0.12f, 0.06f, 0.12f), stone);
                b.Pyramid(V(0, 0.06f, 0), 0.05f, 0.22f, 4, C(0xD0302A));
                b.Pyramid(V(0, 0.2f, 0), 0.03f, 0.2f, 4, white);
                b.Pyramid(V(0, 0.34f, 0), 0.016f, 0.16f, 4, C(0xD0302A));
                b.Gem(V(0.04f, 0.26f, 0.02f), 0.035f, white, 0.4f, 8);
                break;
            case "cathedral":
                Hall(0.16f, 0.2f, 0.26f, C(0xD8D2C4), C(0x5A6A8A));
                b.Box(V(0, 0.2f, 0.1f), V(0.09f, 0.4f, 0.09f), C(0xD8D2C4));
                b.Pyramid(V(0, 0.4f, 0.1f), 0.07f, 0.2f, 4, C(0x5A6A8A), Mathf.Pi / 4);
                b.Box(V(0, 0.64f, 0.1f), V(0.015f, 0.08f, 0.015f), gold); b.Box(V(0, 0.65f, 0.1f), V(0.05f, 0.015f, 0.015f), gold);
                b.Box(V(0, 0.16f, 0.146f), V(0.04f, 0.07f, 0.005f), glass);
                break;
            case "stock_exchange":
                Hall(0.26f, 0.2f, 0.17f, white, C(0x3E8A5A));
                foreach (int i in new[] { -2, -1, 0, 1, 2 }) b.Box(V(i * 0.05f, 0.1f, 0.1f), V(0.022f, 0.2f, 0.022f), white);
                break;
            case "shipyard":
                b.Box(V(0, 0.02f, 0), V(0.3f, 0.04f, 0.16f), C(0x8A8A84));
                b.Box(V(-0.03f, 0.08f, 0.02f), V(0.2f, 0.08f, 0.07f), brick.Darkened(0.2f));
                b.Tri(V(0.07f, 0.04f, -0.015f), V(0.07f, 0.04f, 0.055f), V(0.13f, 0.12f, 0.02f), Vector3.Right, brick.Darkened(0.2f));
                foreach (int s in new[] { -1, 1 }) b.Box(V(s * 0.1f, 0.17f, -0.05f), V(0.02f, 0.3f, 0.02f), C(0xE0A81F));
                b.Box(V(0, 0.32f, -0.05f), V(0.26f, 0.025f, 0.03f), C(0xE0A81F));
                break;
            case "stadium":
                b.Column(V(0, 0, 0), 0.17f, 0.1f, C(0xC8CCD2), 12, 0.19f);
                b.Column(V(0, 0.07f, 0), 0.13f, 0.035f, C(0x4E9A3E), 12);
                b.Box(V(0, 0.108f, 0), V(0.1f, 0.004f, 0.004f), white);
                foreach (int s in new[] { -1, 1 }) b.Box(V(s * 0.16f, 0.16f, 0), V(0.015f, 0.2f, 0.015f), dark);
                break;
            case "laboratory":
                Hall(0.24f, 0.14f, 0.16f, C(0xEEF2F6), C(0xC8D2DC), false);
                b.Box(V(0, 0.08f, 0.081f), V(0.2f, 0.05f, 0.005f), glass);
                b.Gem(V(0.06f, 0.21f, 0), 0.055f, white, 0.5f, 8);
                b.Box(V(0.06f, 0.17f, 0), V(0.015f, 0.05f, 0.015f), dark);
                break;
            case "hospital":
                Hall(0.22f, 0.22f, 0.16f, C(0xF6F6F6), C(0xD0D4DA), false);
                b.Box(V(0, 0.14f, 0.081f), V(0.03f, 0.1f, 0.005f), C(0xE0302A));
                b.Box(V(0, 0.14f, 0.081f), V(0.1f, 0.03f, 0.005f), C(0xE0302A));
                break;
            case "fusion_plant":
                b.Column(V(0, 0, 0), 0.15f, 0.08f, C(0x8A94A2), 10);
                b.Column(V(0, 0.08f, 0), 0.12f, 0.05f, C(0x5A6472), 10, 0.1f);
                b.Gem(V(0, 0.22f, 0), 0.08f, C(0x7FF4FF), 1, 8);
                foreach (int i in new[] { 0, 1, 2 }) { float a = Mathf.Tau * i / 3; b.Box(V(Mathf.Cos(a) * 0.13f, 0.14f, Mathf.Sin(a) * 0.13f), V(0.025f, 0.28f, 0.025f), C(0xC8D2DC)); }
                break;
            case "nanoforge":
                b.Box(V(0, 0.09f, 0), V(0.22f, 0.18f, 0.18f), C(0x23262E));
                foreach (float y in new[] { 0.05f, 0.1f, 0.15f }) b.Box(V(0, y, 0.091f), V(0.18f, 0.012f, 0.004f), C(0x5FF0FF));
                b.Gem(V(0, 0.26f, 0), 0.06f, C(0x5FF0FF), 1.4f, 4);
                break;
            case "orbital_hub":
                b.Box(V(0, 0.03f, 0), V(0.16f, 0.06f, 0.16f), C(0x8A94A2));
                b.Column(V(0, 0.06f, 0), 0.03f, 0.56f, C(0xE4E8EE), 6, 0.012f);
                b.Column(V(0, 0.44f, 0), 0.1f, 0.02f, C(0xB0BCCB), 10);
                b.Gem(V(0, 0.66f, 0), 0.03f, C(0xFF5A4A));
                break;
            case "quantum_lab":
                b.Column(V(0, 0, 0), 0.12f, 0.06f, C(0x3A3550), 8);
                b.Gem(V(0, 0.26f, 0), 0.1f, C(0xB07CFF), 1.2f, 6);
                foreach (int s in new[] { -1, 1 }) b.Box(V(s * 0.11f, 0.12f, 0), V(0.02f, 0.24f, 0.02f), C(0xDCD6F0));
                break;
            case "social_center":
                b.Column(V(0, 0, 0), 0.15f, 0.05f, white, 10);
                b.Pyramid(V(0, 0.05f, 0), 0.14f, 0.16f, 10, C(0xF0A8C8));
                b.Gem(V(0, 0.24f, 0), 0.03f, gold);
                break;
            case "herbalist":
                b.Column(V(0, 0, 0), 0.09f, 0.1f, C(0xA88A5A), 6);
                b.Pyramid(V(0, 0.1f, 0), 0.13f, 0.14f, 6, C(0x4E9A4A));
                b.Gem(V(0.12f, 0.04f, 0.06f), 0.04f, C(0x7FE0A8)); b.Gem(V(-0.11f, 0.04f, 0.08f), 0.035f, C(0xE06A8A));
                break;
            case "totem_pole":
                b.Column(V(0, 0, 0), 0.04f, 0.44f, wood, 6);
                b.Box(V(0, 0.12f, 0), V(0.1f, 0.07f, 0.09f), C(0xD0402E));
                b.Box(V(0, 0.24f, 0), V(0.1f, 0.07f, 0.09f), C(0x3EB8A0));
                b.Box(V(0, 0.36f, 0), V(0.24f, 0.05f, 0.03f), C(0xF2C230));
                break;
            case "stone_circle":
                for (int i = 0; i < 6; i++) { float a = Mathf.Tau * i / 6; b.Box(V(Mathf.Cos(a) * 0.12f, 0.08f, Mathf.Sin(a) * 0.12f), V(0.045f, 0.16f, 0.045f), stone); }
                b.Gem(V(0, 0.12f, 0), 0.04f, C(0x7FE0A8), 1.5f);
                break;
            case "mage_tower":
                b.Column(V(0, 0, 0), 0.07f, 0.4f, C(0x6A5AA8), 8, 0.055f);
                b.Column(V(0, 0.4f, 0), 0.085f, 0.04f, C(0x4A3A8A), 8);
                b.Pyramid(V(0, 0.44f, 0), 0.085f, 0.16f, 8, C(0x3A2C72));
                b.Gem(V(0, 0.68f, 0), 0.045f, C(0xFF8A3A));
                break;
            case "alchemy_lab":
                Hall(0.2f, 0.12f, 0.15f, C(0x8A7A6A), C(0x4A3A5A));
                b.Gem(V(0.05f, 0.27f, 0), 0.05f, C(0x7CFF4A));
                b.Column(V(0.05f, 0.18f, 0), 0.015f, 0.06f, glass, 5);
                break;
            default:
            {
                // buildings from mods: a hall whose roof tells what it is for
                var x = Rules.Building(id)?.X ?? Rules.ModBuilding.None;
                if (x.Bombard > 0) goto case "mage_tower";
                Hall(0.2f, 0.14f, 0.16f, C(0xD8CCB8), x.Power > 0 ? Ui.Power : team);
                if (x.Power > 0) b.Gem(V(0, 0.28f, 0), 0.045f, Ui.Power, 1.5f);
                break;
            }
        }
    }

    // --------------------------------------------------------- improvements
    /// <summary>What workers have built on a tile; drawn in the near-right part of it, clear of resources and units.</summary>
    public static void Improvement(MeshBuilder b, Improvement kind, Vector3 c, bool road)
    {
        var saved = b.Xf;
        b.Xf = saved * new Transform3D(Basis.Identity, c + V(0.2f, 0, 0.36f));
        Color wood = C(0x7A5232), white = C(0xEADFC8);
        switch (kind)
        {
            case IH.Shared.Improvement.Farm:
                for (int i = 0; i < 5; i++)
                    b.Box(V(0, 0.02f, -0.2f + i * 0.1f), V(0.62f, 0.035f, 0.06f), i % 2 == 0 ? C(0xE6D36A) : C(0xB8C24A));
                b.Box(V(-0.44f, 0.08f, 0.08f), V(0.14f, 0.16f, 0.16f), C(0xB5533A));        // barn
                b.Pyramid(V(-0.44f, 0.16f, 0.08f), 0.13f, 0.09f, 4, C(0x6A3A2E), Mathf.Pi / 4);
                break;
            case IH.Shared.Improvement.Mine:
                b.Pyramid(V(0.05f, 0, -0.05f), 0.26f, 0.3f, 5, C(0x5A554E));
                b.Box(V(0.05f, 0.08f, 0.14f), V(0.15f, 0.16f, 0.06f), C(0x15151A));        // the adit
                foreach (int s in new[] { -1, 1 }) b.Box(V(0.05f + s * 0.085f, 0.09f, 0.17f), V(0.03f, 0.18f, 0.03f), wood);
                b.Box(V(0.05f, 0.19f, 0.17f), V(0.22f, 0.03f, 0.03f), wood);
                b.Box(V(-0.22f, 0.05f, 0.2f), V(0.12f, 0.07f, 0.08f), C(0x4A4A50));        // ore cart
                b.Box(V(-0.22f, 0.1f, 0.2f), V(0.09f, 0.03f, 0.05f), C(0xC8743A));
                break;
            case IH.Shared.Improvement.Plantation:
                for (int i = -2; i <= 2; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        b.Column(V(i * 0.14f, 0, -0.14f + j * 0.14f), 0.012f, 0.08f, wood, 4);
                        b.Gem(V(i * 0.14f, 0.11f, -0.14f + j * 0.14f), 0.055f, C(0x3FA34A), 0.9f);
                    }
                break;
            case IH.Shared.Improvement.Quarry:
                b.Box(V(0, 0.03f, 0), V(0.56f, 0.06f, 0.4f), C(0x9A9A92));
                b.Box(V(-0.06f, 0.09f, -0.05f), V(0.4f, 0.06f, 0.28f), C(0xB8B8B0));
                b.Box(V(-0.12f, 0.15f, -0.08f), V(0.24f, 0.06f, 0.18f), C(0xD0D0C8));
                b.Box(V(0.2f, 0.1f, 0.12f), V(0.1f, 0.09f, 0.1f), C(0xE4E4DC));          // a cut block
                break;
            case IH.Shared.Improvement.Pasture:
                foreach (var (px, pz, sx, sz) in new[] { (0f, -0.2f, 0.62f, 0.025f), (0f, 0.2f, 0.62f, 0.025f), (-0.31f, 0f, 0.025f, 0.4f), (0.31f, 0f, 0.025f, 0.4f) })
                {
                    b.Box(V(px, 0.1f, pz), V(sx, 0.02f, sz), wood);
                    b.Box(V(px, 0.05f, pz), V(sx, 0.02f, sz), wood);
                }
                foreach (int sx in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 }) b.Box(V(sx * 0.31f, 0.07f, sz * 0.2f), V(0.035f, 0.14f, 0.035f), wood.Darkened(0.2f));
                b.Box(V(0.14f, 0.04f, 0.05f), V(0.18f, 0.06f, 0.07f), C(0x6A8FB8));        // trough
                break;
            case IH.Shared.Improvement.Camp:
                b.Pyramid(V(-0.08f, 0, 0), 0.2f, 0.26f, 4, C(0xD8C49A), Mathf.Pi / 4);
                b.Tri(V(-0.14f, 0, 0.142f), V(-0.02f, 0, 0.142f), V(-0.08f, 0.14f, 0.09f), Vector3.Back, C(0x3A2C20));
                b.Pyramid(V(0.2f, 0, 0.1f), 0.06f, 0.04f, 6, C(0x4A4A50));
                b.Gem(V(0.2f, 0.08f, 0.1f), 0.04f, C(0xFF8A1F), 1.6f, 4);                // camp fire
                b.Box(V(0.22f, 0.12f, -0.14f), V(0.02f, 0.24f, 0.02f), wood);
                b.Box(V(0.22f, 0.2f, -0.08f), V(0.02f, 0.02f, 0.14f), wood);              // drying rack
                break;
            case IH.Shared.Improvement.FishingBoats:
                foreach (var (x, z) in new[] { (0.05f, 0f), (-0.3f, 0.14f) })
                {
                    b.Box(V(x, 0.04f, z), V(0.26f, 0.07f, 0.1f), wood);
                    b.Tri(V(x + 0.13f, 0.005f, z - 0.05f), V(x + 0.13f, 0.005f, z + 0.05f), V(x + 0.21f, 0.075f, z), Vector3.Right, wood);
                    b.Box(V(x, 0.2f, z), V(0.02f, 0.28f, 0.02f), wood.Darkened(0.2f));
                    b.Tri(V(x + 0.01f, 0.33f, z), V(x + 0.01f, 0.12f, z), V(x + 0.14f, 0.12f, z), Vector3.Back, Colors.White);
                    b.Tri(V(x + 0.01f, 0.33f, z), V(x + 0.01f, 0.12f, z), V(x + 0.14f, 0.12f, z), Vector3.Forward, Colors.White);
                }
                break;
            case IH.Shared.Improvement.OilWell:
                b.Box(V(0, 0.02f, 0), V(0.3f, 0.04f, 0.3f), C(0x55504A));
                foreach (int sx in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 })
                    b.Quad(V(sx * 0.12f - 0.012f, 0.04f, sz * 0.12f), V(sx * 0.12f + 0.012f, 0.04f, sz * 0.12f), V(sx * 0.02f + 0.012f, 0.56f, sz * 0.02f), V(sx * 0.02f - 0.012f, 0.56f, sz * 0.02f), V(0, 0, sz), C(0x2A2A2A));
                b.Box(V(0, 0.3f, 0), V(0.16f, 0.015f, 0.16f), C(0x2A2A2A));
                b.Box(V(0, 0.57f, 0), V(0.07f, 0.03f, 0.07f), C(0xD0302A));
                b.Column(V(0.24f, 0, 0.1f), 0.07f, 0.14f, C(0x8A8A90), 8);                 // tank
                break;
            case IH.Shared.Improvement.Cottage:
                foreach (var (x, z, h, roof) in new[] { (-0.12f, -0.04f, 0.14f, 0xA04030), (0.12f, 0.02f, 0.11f, 0x8A3A2A), (0f, 0.17f, 0.1f, 0xB5533A) })
                {
                    b.Box(V(x, h / 2, z), V(0.17f, h, 0.14f), white);
                    b.Pyramid(V(x, h, z), 0.14f, 0.1f, 4, C(roof), Mathf.Pi / 4);
                }
                break;
        }
        b.Xf = saved;
    }
}
