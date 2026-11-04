using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    /// <summary>
    /// The commands exposed to Claude. Every method runs on Revit's main thread.
    /// Lengths going in/out are millimetres; Revit's internal unit is feet.
    /// </summary>
    internal static class Commands
    {
        public static object Run(UIApplication app, string command, JsonElement args)
        {
            if (command == "ping")
            {
                return new
                {
                    revit = app.Application.VersionName,
                    build = app.Application.VersionBuild,
                    activeDocument = app.ActiveUIDocument?.Document?.Title,
                };
            }

            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("No document is open in Revit.");
            var doc = uidoc.Document;

            return command switch
            {
                "get_project_info" => ProjectInfo(doc),
                "get_active_view" => ViewInfo(doc.ActiveView),
                "list_views" => ListViews(doc, Str(args, "viewType")),
                "list_levels" => ListLevels(doc),
                "list_categories" => ListCategories(doc),
                "get_elements" => GetElements(doc, Str(args, "category"), Bool(args, "viewOnly"), Int(args, "limit") ?? 200),
                "get_selection" => uidoc.Selection.GetElementIds().Select(id => Summary(doc.GetElement(id))).ToList(),
                "select_elements" => Select(uidoc, Ids(args, "ids")),
                "get_element_parameters" => Parameters(Get(doc, Id(args, "id"))),
                "set_element_parameter" => SetParameter(doc, args),
                "list_wall_types" => new FilteredElementCollector(doc).OfClass(typeof(WallType))
                    .Select(t => new { id = t.Id.Value, name = t.Name }).ToList(),
                "create_wall" => CreateWall(doc, args),
                "create_level" => CreateLevel(doc, args),
                "delete_elements" => Delete(doc, Ids(args, "ids")),
                "get_rooms" => GetRooms(doc),
                _ => throw new ArgumentException($"Unknown command '{command}'"),
            };
        }

        // ---------- Read commands ----------

        private static object ProjectInfo(Document doc)
        {
            var pi = doc.ProjectInformation;
            return new
            {
                title = doc.Title,
                path = doc.PathName,
                isWorkshared = doc.IsWorkshared,
                name = pi?.Name,
                number = pi?.Number,
                client = pi?.ClientName,
                address = pi?.Address,
                status = pi?.Status,
                lengthUnit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId,
            };
        }

        private static object ViewInfo(View v) => v == null ? null : new
        {
            id = v.Id.Value,
            name = v.Name,
            viewType = v.ViewType.ToString(),
            level = v.GenLevel?.Name,
            scale = v.Scale,
            isTemplate = v.IsTemplate,
        };

        private static object ListViews(Document doc, string viewType)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate);
            if (!string.IsNullOrEmpty(viewType))
                views = views.Where(v => v.ViewType.ToString().Equals(viewType, StringComparison.OrdinalIgnoreCase));
            return views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).Select(ViewInfo).ToList();
        }

        private static object ListLevels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new { id = l.Id.Value, name = l.Name, elevationMm = Math.Round(ToMm(l.Elevation), 1) })
                .ToList();

        private static object ListCategories(Document doc) =>
            new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .GroupBy(e => e.Category.BuiltInCategory)
                .Select(g => new { category = g.Key.ToString(), name = g.First().Category.Name, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();

        private static object GetElements(Document doc, string category, bool viewOnly, int limit)
        {
            if (string.IsNullOrEmpty(category)) throw new ArgumentException("'category' is required, e.g. OST_Walls");
            if (!category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) category = "OST_" + category;
            if (!Enum.TryParse(category, true, out BuiltInCategory bic))
                throw new ArgumentException($"Unknown category '{category}'");

            var collector = viewOnly
                ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                : new FilteredElementCollector(doc);
            var all = collector.OfCategory(bic).WhereElementIsNotElementType().ToElements();
            return new
            {
                total = all.Count,
                returned = Math.Min(all.Count, limit),
                elements = all.Take(limit).Select(Summary).ToList(),
            };
        }

        private static object GetRooms(Document doc) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>()
                .Select(r => new
                {
                    id = r.Id.Value,
                    number = r.Number,
                    name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                    level = r.Level?.Name,
                    areaM2 = Math.Round(UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters), 2),
                    volumeM3 = Math.Round(UnitUtils.ConvertFromInternalUnits(r.Volume, UnitTypeId.CubicMeters), 2),
                    placed = r.Location != null,
                })
                .ToList();

        private static object Summary(Element e)
        {
            if (e == null) return null;
            var typeId = e.GetTypeId();
            var type = typeId != ElementId.InvalidElementId ? e.Document.GetElement(typeId) : null;
            var levelId = e.LevelId;
            return new
            {
                id = e.Id.Value,
                name = e.Name,
                category = e.Category?.Name,
                type = type?.Name,
                family = (type as FamilySymbol)?.FamilyName,
                level = levelId != ElementId.InvalidElementId ? e.Document.GetElement(levelId)?.Name : null,
            };
        }

        private static object Parameters(Element e) => new
        {
            element = Summary(e),
            parameters = e.Parameters.Cast<Parameter>()
                .Where(p => p.Definition != null)
                .OrderBy(p => p.Definition.Name)
                .Select(p => new
                {
                    name = p.Definition.Name,
                    value = ParamValue(p),
                    storageType = p.StorageType.ToString(),
                    readOnly = p.IsReadOnly,
                })
                .ToList(),
        };

        private static string ParamValue(Parameter p)
        {
            if (!p.HasValue) return null;
            return p.StorageType switch
            {
                StorageType.String => p.AsString(),
                StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
                _ => p.AsValueString() ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.AsDouble().ToString()),
            };
        }

        private static object Select(UIDocument uidoc, List<ElementId> ids)
        {
            uidoc.Selection.SetElementIds(ids);
            if (ids.Count > 0) uidoc.ShowElements(ids);
            return new { selected = ids.Count };
        }

        // ---------- Write commands (each in its own transaction, undoable with Ctrl+Z) ----------

        private static object SetParameter(Document doc, JsonElement args)
        {
            var e = Get(doc, Id(args, "id"));
            var name = Str(args, "parameter") ?? throw new ArgumentException("'parameter' is required");
            var p = e.LookupParameter(name) ?? throw new ArgumentException($"Element has no parameter '{name}'");
            if (p.IsReadOnly) throw new InvalidOperationException($"Parameter '{name}' is read-only");
            bool raw = Bool(args, "raw");
            var value = args.GetProperty("value");

            using var t = new Transaction(doc, $"MCP: set {name}");
            t.Start();
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.String:
                    ok = p.Set(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString());
                    break;
                case StorageType.Integer:
                    ok = value.ValueKind == JsonValueKind.Number ? p.Set(value.GetInt32()) : p.SetValueString(value.GetString());
                    break;
                case StorageType.ElementId:
                    ok = p.Set(new ElementId(value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.Parse(value.GetString())));
                    break;
                case StorageType.Double:
                    if (value.ValueKind == JsonValueKind.String)
                        ok = p.SetValueString(value.GetString()); // e.g. "3000 mm" or "10'"
                    else
                    {
                        double d = value.GetDouble();
                        if (!raw && p.Definition.GetDataType() == SpecTypeId.Length) d = FromMm(d);
                        ok = p.Set(d);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Parameter has no storage type");
            }
            if (!ok) { t.RollBack(); throw new InvalidOperationException($"Revit rejected the value for '{name}'"); }
            t.Commit();
            return new { id = e.Id.Value, parameter = name, newValue = ParamValue(p) };
        }

        private static object CreateWall(Document doc, JsonElement args)
        {
            var start = new XYZ(FromMm(Num(args, "startX")), FromMm(Num(args, "startY")), 0);
            var end = new XYZ(FromMm(Num(args, "endX")), FromMm(Num(args, "endY")), 0);
            if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance)
                throw new ArgumentException("Wall is too short");

            var levelId = OptId(args, "levelId") ?? new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault()?.Id
                ?? throw new InvalidOperationException("Project has no levels");
            var level = (Level)Get(doc, levelId);
            var typeId = OptId(args, "wallTypeId") ?? doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
            double height = FromMm(OptNum(args, "heightMm") ?? 3000);

            using var t = new Transaction(doc, "MCP: create wall");
            t.Start();
            var line = Line.CreateBound(new XYZ(start.X, start.Y, level.Elevation), new XYZ(end.X, end.Y, level.Elevation));
            var wall = Wall.Create(doc, line, typeId, levelId, height, 0, false, Bool(args, "structural"));
            t.Commit();
            return Summary(wall);
        }

        private static object CreateLevel(Document doc, JsonElement args)
        {
            using var t = new Transaction(doc, "MCP: create level");
            t.Start();
            var level = Level.Create(doc, FromMm(Num(args, "elevationMm")));
            var name = Str(args, "name");
            if (!string.IsNullOrEmpty(name)) level.Name = name;
            t.Commit();
            return new { id = level.Id.Value, name = level.Name, elevationMm = Math.Round(ToMm(level.Elevation), 1) };
        }

        private static object Delete(Document doc, List<ElementId> ids)
        {
            using var t = new Transaction(doc, "MCP: delete elements");
            t.Start();
            var deleted = doc.Delete(ids);
            t.Commit();
            return new { requested = ids.Count, deletedIncludingDependents = deleted.Count };
        }

        // ---------- Helpers ----------

        private static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        private static double FromMm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        private static Element Get(Document doc, ElementId id) =>
            doc.GetElement(id) ?? throw new ArgumentException($"No element with id {id.Value}");

        private static bool Has(JsonElement args, string name, out JsonElement v)
        {
            v = default;
            return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;
        }

        private static string Str(JsonElement a, string n) => Has(a, n, out var v) ? v.ToString() : null;
        private static bool Bool(JsonElement a, string n) => Has(a, n, out var v) && v.ValueKind == JsonValueKind.True;
        private static int? Int(JsonElement a, string n) => Has(a, n, out var v) ? v.GetInt32() : null;
        private static double? OptNum(JsonElement a, string n) => Has(a, n, out var v) ? v.GetDouble() : null;
        private static double Num(JsonElement a, string n) => OptNum(a, n) ?? throw new ArgumentException($"'{n}' is required");
        private static ElementId OptId(JsonElement a, string n) => Has(a, n, out var v) ? new ElementId(v.GetInt64()) : null;
        private static ElementId Id(JsonElement a, string n) => OptId(a, n) ?? throw new ArgumentException($"'{n}' is required");

        private static List<ElementId> Ids(JsonElement a, string n) =>
            Has(a, n, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => new ElementId(x.GetInt64())).ToList()
                : throw new ArgumentException($"'{n}' must be an array of element ids");
    }
}
