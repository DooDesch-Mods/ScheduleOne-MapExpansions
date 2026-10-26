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

        /// <summary>Names written on the map like the main map's building and district names.</summary>
        public List<MapLabel> Labels { get; } = new();

        /// <summary>
        /// Colour of the expansion's terrain where the map shows it outside the sheet, between the sheets. The default is
        /// the sand of the prologue map.
        /// </summary>
        public Color32 Land { get; set; } = new Color32(214, 163, 117, 255);

        /// <summary>Colour of the expansion's other ground (rocks, cliffs) outside the sheet. The default is the prologue map's rock.</summary>
        public Color32 Rock { get; set; } = new Color32(84, 77, 68, 255);
    }

    /// <summary>How a name on the phone map looks; both show once the player zooms in, like the main map's names.</summary>
    public enum MapLabelStyle
    {
        /// <summary>Grey and centred on the point, like "Gas-Mart" or "Town Hall".</summary>
        Building,

        /// <summary>Coloured and hanging below the point, like "Canal" or "Suburbia Park".</summary>
        District,
    }

    /// <summary>A name on the phone map at a local x/z point. A line break splits it in two lines, as the main map does.</summary>
    public sealed class MapLabel
    {
        public string Text { get; set; }

        public Vector2 Position { get; set; }

        public MapLabelStyle Style { get; set; }

        /// <summary>Colour of the text; null keeps the colour of the main map's names of that style.</summary>
        public Color? Color { get; set; }

        public MapLabel(string text, Vector2 position, MapLabelStyle style = MapLabelStyle.Building, Color? color = null)
        {
            Text = text;
            Position = position;
            Style = style;
            Color = color;
        }
    }
}
