using UnityEngine;

namespace MapExpansions
{
    /// <summary>Where an expansion's map picture comes from.</summary>
    public enum MapPictureSource
    {
        /// <summary>The game's own map of the prologue, drawn in the main map's style.</summary>
        PrologueMap,

        /// <summary>A PNG the expansion supplies in <see cref="MapSheetDefinition.Png"/>.</summary>
        Png,
    }

    /// <summary>
    /// The expansion's part of the phone map: a square picture laid onto the map where the expansion stands. The map
    /// grows to hold every expansion's sheet.
    /// </summary>
    public sealed class MapSheetDefinition
    {
        public MapPictureSource Picture { get; set; } = MapPictureSource.PrologueMap;

        /// <summary>The picture for <see cref="MapPictureSource.Png"/>; north of the picture is local +z.</summary>
        public byte[] Png { get; set; }

        /// <summary>The local x/z point the middle of the picture shows.</summary>
        public Vector2 Centre { get; set; }

        /// <summary>Metres from the middle of the picture to each of its edges.</summary>
        public float HalfSize { get; set; }

        /// <summary>Names written on the map like the main map's district names.</summary>
        public List<MapLabel> Labels { get; } = new();
    }

    /// <summary>A name on the phone map at a local x/z point.</summary>
    public sealed class MapLabel
    {
        public string Text { get; set; }

        public Vector2 Position { get; set; }

        public MapLabel(string text, Vector2 position)
        {
            Text = text;
            Position = position;
        }
    }
}
