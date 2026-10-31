namespace MapExpansions
{
    /// <summary>An additional vanilla loading dock on an existing property, copied with its parking and access points.</summary>
    public sealed class PropertyDockDefinition
    {
        /// <summary>Permanent ID within the property extension. It determines the dock and parking GUIDs.</summary>
        public string Id { get; set; }
        /// <summary>Permanent zero-based slot in Property.LoadingDocks. It must be the next slot when applied.</summary>
        public int Index { get; set; } = -1;
        /// <summary>Existing property supplying a complete dock hierarchy.</summary>
        public string TemplatePropertyCode { get; set; } = "bungalow";
        /// <summary>Zero-based dock in the template property.</summary>
        public int TemplateDockIndex { get; set; }
        /// <summary>World-space position and yaw of the copied dock root.</summary>
        public LocalPoint At { get; set; }
        /// <summary>Optional world-space road entry override; null retains the transformed template point.</summary>
        public LocalPoint? Entry { get; set; }
        /// <summary>Optional world-space road exit override; null retains the transformed template point.</summary>
        public LocalPoint? Exit { get; set; }
        /// <summary>Optional world-space access point used by hidden delivery vehicles.</summary>
        public LocalPoint? HiddenVehicleAccess { get; set; }
    }
}
