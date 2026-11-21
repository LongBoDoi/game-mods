using System;

namespace OC2Tweaks
{
    /// <summary>
    /// The game's own option rows (<see cref="ToggleOption"/>, <see cref="SelectorOption"/>) talk
    /// to an <see cref="IOption"/>. These bridge that interface to the mod's config entries, so a
    /// cloned row behaves exactly like the game's - mouse, keyboard and pad, left/right arrows,
    /// sounds - but reads and writes the mod's settings. Writes are saved straight away; the
    /// screen has no Save button because nothing here needs undoing.
    /// </summary>
    internal sealed class ToggleBridge : IOption
    {
        private readonly Func<bool> read;
        private readonly Action<bool> write;
        internal ToggleBridge(Func<bool> read, Action<bool> write) { this.read = read; this.write = write; }

        public OptionsData.Categories Category { get { return OptionsData.Categories.System; } }
        public string Label { get { return "OC2Tweaks"; } }
        public void SetOption(int value)
        {
            bool on = value == 1;
            if (on != read()) write(on);
        }
        public int GetOption() { return read() ? 1 : 0; }
        public void Commit() {}
    }

    internal sealed class ChoiceBridge : INameListOption
    {
        private readonly Func<string[]> names;
        private readonly Func<int> read;
        private readonly Action<int> write;
        /// <summary>Set once the row exists: <see cref="SelectorOption"/> only redraws itself
        /// through the game's options menu, which this screen does not have.</summary>
        internal Action Changed;

        internal ChoiceBridge(Func<string[]> names, Func<int> read, Action<int> write)
        { this.names = names; this.read = read; this.write = write; }

        public OptionsData.Categories Category { get { return OptionsData.Categories.System; } }
        public string Label { get { return "OC2Tweaks"; } }
        public string[] GetNames() { return names(); }
        public void SetOption(int value)
        {
            int count = names().Length;
            if (count == 0) return;
            value = ((value % count) + count) % count;
            if (value != read()) write(value);
            if (Changed != null) Changed();
        }
        public int GetOption()
        {
            int count = names().Length;
            int value = read();
            return value < 0 || value >= count ? 0 : value;
        }
        public void Commit() {}
    }
}
