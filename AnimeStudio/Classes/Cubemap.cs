using System.Collections.Generic;

namespace AnimeStudio
{
    /// <summary>
    /// Unity Cubemap texture. Genshin stores reflection cubemaps as externally
    /// streamed payloads; preserve their real Cubemap identity and stream data.
    /// </summary>
    public sealed class Cubemap : Texture
    {
        public int m_Width;
        public int m_Height;
        public int m_CompleteImageSize;
        public TextureFormat m_TextureFormat;
        public bool m_MipMap;
        public int m_MipCount;
        public bool m_IsReadable;
        public bool m_IsPreProcessed;
        public bool m_IgnoreMasterTextureLimit;
        public bool m_StreamingMipmaps;
        public int m_StreamingMipmapsPriority;
        public int m_ImageCount;
        public int m_TextureDimension;
        public GLTextureSettings m_TextureSettings;
        public int m_LightmapFormat;
        public int m_ColorSpace;
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;
        public List<PPtr<Texture2D>> m_SourceTextures = new();

        public Cubemap(ObjectReader reader) : base(reader)
        {
            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            m_CompleteImageSize = reader.ReadInt32();
            m_TextureFormat = (TextureFormat)reader.ReadInt32();

            if (version[0] < 5 || (version[0] == 5 && version[1] < 2))
                m_MipMap = reader.ReadBoolean();
            else
                m_MipCount = reader.ReadInt32();

            if (version[0] > 2 || (version[0] == 2 && version[1] >= 6))
                m_IsReadable = reader.ReadBoolean();
            if (version[0] >= 2020)
                m_IsPreProcessed = reader.ReadBoolean();
            if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3))
                m_IgnoreMasterTextureLimit = reader.ReadBoolean();
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2))
            {
                reader.AlignStream(); // m_IgnoreMipmapLimit
                var m_MipmapLimitGroupName = reader.ReadAlignedString();
            }
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2))
                m_StreamingMipmaps = reader.ReadBoolean();
            reader.AlignStream();

            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2))
                m_StreamingMipmapsPriority = reader.ReadInt32();

            m_ImageCount = reader.ReadInt32();
            m_TextureDimension = reader.ReadInt32();
            m_TextureSettings = new GLTextureSettings(reader);
            if (version[0] >= 3)
                m_LightmapFormat = reader.ReadInt32();
            if (version[0] > 3 || (version[0] == 3 && version[1] >= 5))
                m_ColorSpace = reader.ReadInt32();

            // Verified against original GI 7.1 Cubemap objects: unlike Texture2D,
            // Cubemap proceeds directly to image-data size here (no platform blob).
            var imageDataSize = reader.ReadInt32();
            if (imageDataSize == 0 && ((version[0] == 5 && version[1] >= 3) || version[0] > 5))
                m_StreamData = new StreamingInfo(reader);

            if (!string.IsNullOrEmpty(m_StreamData?.path))
            {
                image_data = new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size);
            }
            else
            {
                image_data = new ResourceReader(reader, reader.BaseStream.Position, imageDataSize);
                reader.Position += imageDataSize;
                reader.AlignStream();
            }

            // GI 7.1 serializes the six source-face Texture2D references after the
            // inline/streamed payload descriptor. They are commonly null for runtime
            // cubemaps, but must be preserved when present.
            if (reader.Game.Type.IsGISubGroup())
            {
                var sourceTextureCount = reader.ReadInt32();
                for (var i = 0; i < sourceTextureCount; i++)
                    m_SourceTextures.Add(new PPtr<Texture2D>(reader));
            }
        }
    }
}
