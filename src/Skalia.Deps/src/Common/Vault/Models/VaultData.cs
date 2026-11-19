using System.Collections.Generic;

namespace SkaliaDeps
{
    public sealed class VaultData
    {
        public int Width { get; set; } = 6;

        public int Height { get; set; } = 6;

        public List<VaultItemData> Items { get; set; } = new List<VaultItemData>();

        public VaultData Clone()
        {
            var clone = new VaultData
            {
                Width = Width,
                Height = Height
            };

            foreach (var item in Items)
            {
                clone.Items.Add(item.Clone());
            }

            return clone;
        }
    }

    public sealed class VaultItemData
    {
        public byte X { get; set; }

        public byte Y { get; set; }

        public byte Rotation { get; set; }

        public ushort ItemId { get; set; }

        public byte Amount { get; set; }

        public byte Quality { get; set; }

        public string StateBase64 { get; set; } = string.Empty;

        public VaultItemData Clone()
        {
            return new VaultItemData
            {
                X = X,
                Y = Y,
                Rotation = Rotation,
                ItemId = ItemId,
                Amount = Amount,
                Quality = Quality,
                StateBase64 = StateBase64
            };
        }
    }
}
