# Vertex.Gui

Program.cs hosts the desktop window and serves embedded resources through Photino. The Next.js source lives in UserInterface/. Coordinate changes to desktop messages with UserInterface/app/backend.ts and verify the affected interaction in the desktop app.

From the Vertex repository root, run `dotnet build Vertex.Gui/Vertex.Gui.csproj`.
