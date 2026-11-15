global using Sandbox;
global using System;
global using System.Collections.Generic;
global using System.Linq;

// Vehicle: the vendored Vehicle Physics Kit (Libraries/fieldguide.vehiclephysics, MIT) plus the small
// game-side roster and part-kit assembler in Code/Vehicle (namespace VehicleProto).
global using VehicleProto;
global using FieldGuide.VehiclePhysics;

// Both namespaces declare a CarDefinitions roster (the kit ships a blockout demo roster; this game keeps
// its own tuned roster with part-kit body manifests in Code/Vehicle/CarRoster.cs). Pin the game roster.
global using CarDefinitions = VehicleProto.CarDefinitions;
