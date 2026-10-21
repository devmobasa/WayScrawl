using System.Runtime.InteropServices;

namespace WayScrawl.Wayland.Interop;

// Translates wl_keyboard keycodes with the keymap the compositor sent, so shortcuts follow the
// active layout (and virtual keyboards with their own keymaps) instead of physical evdev codes.
internal sealed partial class Xkb : IDisposable
{
    private const string Library = "libxkbcommon.so.0";
    private const int KeymapFormatTextV1 = 1, StateModsEffective = 8;
    private nint context, keymap, state;

    internal const uint Escape = 0xff1b, Delete = 0xffff, BackSpace = 0xff08;

    [LibraryImport(Library)] private static partial nint xkb_context_new(int flags);
    [LibraryImport(Library)] private static partial void xkb_context_unref(nint context);
    [LibraryImport(Library)] private static partial nint xkb_keymap_new_from_buffer(nint context, nint buffer, nuint length, int format, int flags);
    [LibraryImport(Library)] private static partial void xkb_keymap_unref(nint keymap);
    [LibraryImport(Library)] private static partial nint xkb_state_new(nint keymap);
    [LibraryImport(Library)] private static partial void xkb_state_unref(nint state);
    [LibraryImport(Library)] private static partial int xkb_state_update_mask(nint state, uint depressedMods, uint latchedMods,
        uint lockedMods, uint depressedLayout, uint latchedLayout, uint lockedLayout);
    [LibraryImport(Library)] private static partial uint xkb_state_key_get_one_sym(nint state, uint key);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int xkb_state_mod_name_is_active(nint state, string name, int type);
    [LibraryImport(Library)] private static partial uint xkb_keysym_to_lower(uint keysym);

    internal bool Ready => state != 0;

    // Takes ownership of fd and always closes it. Returns false if the keymap is unusable.
    internal bool Load(int format, int fd, uint size)
    {
        try
        {
            if (format != KeymapFormatTextV1 || size == 0) return false;
            if (context == 0) context = xkb_context_new(0);
            nint text = Native.MapReadOnly(fd, (int)size);
            nint next;
            try
            {
                // The buffer is NUL-terminated; xkbcommon wants the length without it.
                next = xkb_keymap_new_from_buffer(context, text, size - 1, KeymapFormatTextV1, 0);
            }
            finally { Native.Unmap(text, (int)size); }
            if (next == 0) return false;
            Release();
            keymap = next;
            state = xkb_state_new(keymap);
            return state != 0;
        }
        catch (DllNotFoundException) { return false; }
        finally { Native.Close(fd); }
    }

    internal void UpdateModifiers(uint depressed, uint latched, uint locked, uint group)
    {
        if (state != 0) xkb_state_update_mask(state, depressed, latched, locked, 0, 0, group);
    }

    // Lower-cased keysym for a wl_keyboard keycode (evdev code; xkb adds 8).
    internal uint Keysym(uint key) => state == 0 ? 0 : xkb_keysym_to_lower(xkb_state_key_get_one_sym(state, key + 8));

    internal bool Control => state != 0 && xkb_state_mod_name_is_active(state, "Control", StateModsEffective) > 0;

    private void Release()
    {
        if (state != 0) xkb_state_unref(state);
        if (keymap != 0) xkb_keymap_unref(keymap);
        state = keymap = 0;
    }

    public void Dispose()
    {
        Release();
        if (context != 0) xkb_context_unref(context);
        context = 0;
    }
}
