namespace AnimeStudio
{
    /// <summary>
    /// Minimal ParticleSystemRenderer parser for resolved model closure.
    /// The Unity Renderer base owns the authoritative material list and renderer PPtrs;
    /// derived particle-renderer fields are intentionally left raw/provenance-only for now.
    /// </summary>
    public sealed class ParticleSystemRenderer : Renderer
    {
        // The fork does not yet model the complete Unity ParticleSystemRenderer tail.
        // Remember where the Renderer base ends so the resolved exporter can recover
        // exact contextual Mesh PPtrs from the remaining serialized bytes without
        // pretending the rest of the derived schema has been parsed.
        public int m_DerivedDataOffset;

        public ParticleSystemRenderer(ObjectReader reader) : base(reader)
        {
            m_DerivedDataOffset = (int)(reader.Position - reader.byteStart);
        }
    }
}
