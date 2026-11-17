# Fluent toolbar artwork

These are Microsoft Fluent System Icons (Regular), under the adjacent MIT
license. `fluent-source.json` records the upstream revision and action mapping;
`svg/` keeps the original size-specific vector drawings.

`fluent-*.png` are transparent, white icon atlases, eight cells per row, in
`fluent-names.txt` order. The application tints and caches individual cells.
Common desktop DPI sizes are embedded directly; other sizes are reduced from
the next larger atlas. Custom recording symbols and drawing stamps keep their
existing GDI+ renderer.

Normal builds use the checked-in atlases and need only the Windows C# compiler.
To regenerate artwork, run `py -3 tools/make-fluent-icons.py` with Edge or Chrome
installed. This optional design-time step downloads assets from the pinned
upstream revision; the application itself does not use a browser or network to
draw icons.
