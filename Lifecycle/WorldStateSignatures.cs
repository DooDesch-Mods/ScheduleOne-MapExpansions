using System.Globalization;
using UnityEngine;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// Text forms of definitions, equal exactly when applying them builds the same thing. Computed when a state is
    /// applied, because a definition can change between registration and use.
    /// </summary>
    internal static class WorldStateSignatures
    {
        private const string Separator = "\u001f";

        private static string N(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => N(v.x) + "," + N(v.y) + "," + N(v.z);
        private static string B(Bounds b) => V(b.center) + "/" + V(b.size);
        private static string P(LocalPoint p) => V(p.Position) + "@" + N(p.Yaw);
        private static string P(LocalPoint? p) => p.HasValue ? P(p.Value) : "-";
        private static string Join(IEnumerable<string> parts) => string.Join(Separator, parts);

        internal static string Of(CopyDefinition c) => Join(new[]
        {
            c.Id, c.Template, c.Resource, c.ResourceRoot, c.VisualOnly.ToString(), c.LightsOn?.ToString(), c.LightRange?.ToString("R", CultureInfo.InvariantCulture), c.LightIntensity?.ToString("R", CultureInfo.InvariantCulture), c.LightSwitch, c.Frame, P(c.At), c.Onto,
            V(c.OntoOffset), V(c.OntoRotation), N(c.Scale), c.OwnMesh.ToString(), c.IndependentMesh.ToString(), c.TemplateSide.ToString(),
            c.Model, c.Collider.ToString(), c.Prefab, c.Obstacle?.ToString(), c.Added.ToString(), Join(c.Exclude),
            Join(c.Boxes.Select(b => Join(new[] { b.Id, b.Kind.ToString(), B(b.Box), b.Material, N(b.TileMetres) }))),
        });

        internal static string Of(PropertyExtensionDefinition e) => Join(new[]
        {
            e.Id, e.PropertyCode, e.GridIndex.ToString(CultureInfo.InvariantCulture), e.GridOrigin.HasValue ? V(e.GridOrigin.Value) : "-",
            Join(e.Tiles.Select(t => (t.X, t.Y)).OrderBy(t => t.X).ThenBy(t => t.Y).Select(t => FormattableString.Invariant($"{t.X},{t.Y}"))),
            Join(e.Bounds.Select(B)),
            Join(e.Surfaces.Select(s => Join(new[] { s.Id, s.CopyId, s.BoxId, B(s.Bounds), s.SurfaceType.ToString(), Join(s.ValidFaces.Select(f => f.ToString())) }))),
            Join(e.Docks.Select(d => Join(new[]
            {
                d.Id, d.Index.ToString(CultureInfo.InvariantCulture), d.TemplatePropertyCode, d.TemplateDockIndex.ToString(CultureInfo.InvariantCulture),
                P(d.At), P(d.Entry), P(d.Exit), P(d.HiddenVehicleAccess),
            }))),
            Join(e.Fixtures.Select(f => Join(new[]
            {
                f.Id, f.Path,
                Join(f.Tiles.Select(t => (t.X, t.Y)).OrderBy(t => t.X).ThenBy(t => t.Y).Select(t => FormattableString.Invariant($"{t.X},{t.Y}"))),
            }))),
        });

        /// <summary>The copies whose blocks carry surfaces of this extension.</summary>
        internal static IReadOnlyCollection<string> Carriers(PropertyExtensionDefinition e) =>
            e.Surfaces.Where(s => !string.IsNullOrEmpty(s.CopyId)).Select(s => s.CopyId).Distinct(StringComparer.Ordinal).ToList();
    }
}
