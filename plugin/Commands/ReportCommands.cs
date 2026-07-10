using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    internal static class ReportCommands
    {
        // create_clash_report:
        //   tests        optional list of test names/GUIDs (default: all)
        //   format       html | csv | json   (default html)
        //   status       optional status filter (e.g. only Active)
        //   outputPath   optional explicit file path; defaults to
        //                Documents\AutoNAV Reports\<doc>_<timestamp>.<ext>
        //   includeImages  html only; embeds clash viewpoint images (base64)
        public static object CreateClashReport(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);

            string format = (CommandRouter.GetString(args, "format", "html") ?? "html").ToLowerInvariant();
            if (format != "html" && format != "csv" && format != "json")
                throw new CommandException("Invalid format '" + format + "'. Valid formats: html, csv, json.");

            string statusFilter = CommandRouter.GetString(args, "status");
            ClashResultStatus? status = null;
            if (!string.IsNullOrEmpty(statusFilter)) status = ClashHelpers.ParseStatus(statusFilter);

            bool includeImages = CommandRouter.GetBool(args, "includeImages", false) && format == "html";

            List<string> testNames = CommandRouter.GetStringList(args, "tests");
            var tests = new List<ClashTest>();
            if (testNames == null || testNames.Count == 0)
                tests.AddRange(ClashCompat.EnumerateTests(clash.TestsData));
            else
                foreach (string name in testNames)
                    tests.Add(ClashHelpers.FindTest(clash.TestsData, name));
            if (tests.Count == 0)
                throw new CommandException("No clash tests found in the document.");

            string path = CommandRouter.GetString(args, "outputPath");
            if (string.IsNullOrEmpty(path))
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoNAV Reports");
                Directory.CreateDirectory(dir);
                string docName = string.IsNullOrEmpty(doc.Title) ? "Untitled" : doc.Title;
                foreach (char bad in Path.GetInvalidFileNameChars()) docName = docName.Replace(bad, '_');
                path = Path.Combine(dir, string.Format(CultureInfo.InvariantCulture,
                    "{0}_ClashReport_{1:yyyyMMdd_HHmmss}.{2}", docName, DateTime.Now, format));
            }
            else
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            // Gather rows per test.
            var testBlocks = new List<KeyValuePair<ClashTest, List<KeyValuePair<ClashResult, string>>>>();
            int totalRows = 0;
            foreach (ClashTest test in tests)
            {
                var rows = ClashHelpers.IterateResults(test)
                    .Where(p => status == null || p.Key.Status == status.Value)
                    .ToList();
                totalRows += rows.Count;
                testBlocks.Add(new KeyValuePair<ClashTest, List<KeyValuePair<ClashResult, string>>>(test, rows));
            }

            switch (format)
            {
                case "csv": WriteCsv(doc, path, testBlocks); break;
                case "json": WriteJson(doc, path, testBlocks); break;
                default: WriteHtml(doc, clash, path, testBlocks, includeImages); break;
            }

            return new Dictionary<string, object>
            {
                { "path", path },
                { "format", format },
                { "tests", tests.Count },
                { "clashes", totalRows },
            };
        }

        // ── CSV ──────────────────────────────────────────────────────

        private static void WriteCsv(Document doc, string path,
            List<KeyValuePair<ClashTest, List<KeyValuePair<ClashResult, string>>>> blocks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Test,Group,Clash,Status,Distance,Grid Location,Assigned To,Approved By,Description,Item 1,Item 1 File,Item 2,Item 2 File,Created");
            foreach (var block in blocks)
            {
                foreach (var pair in block.Value)
                {
                    ClashResult r = pair.Key;
                    ModelItem i1 = r.CompositeItem1 ?? r.Item1;
                    ModelItem i2 = r.CompositeItem2 ?? r.Item2;
                    sb.AppendLine(string.Join(",", new[]
                    {
                        Csv(block.Key.DisplayName), Csv(pair.Value), Csv(r.DisplayName),
                        Csv(r.Status.ToString()),
                        Csv(r.Distance.ToString("0.###", CultureInfo.InvariantCulture)),
                        Csv(ClashHelpers.DescribeGridLocation(doc, r.Center)),
                        Csv(ClashCompat.GetAssignedTo(r)), Csv(ClashCompat.GetApprovedBy(r)),
                        Csv(r.Description),
                        Csv(ClashHelpers.DescribeItem(i1)), Csv(ClashHelpers.GetFileAncestorName(i1)),
                        Csv(ClashHelpers.DescribeItem(i2)), Csv(ClashHelpers.GetFileAncestorName(i2)),
                        Csv(r.CreatedTime.HasValue ? r.CreatedTime.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : ""),
                    }));
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string value)
        {
            value = value ?? "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        // ── JSON ─────────────────────────────────────────────────────

        private static void WriteJson(Document doc, string path,
            List<KeyValuePair<ClashTest, List<KeyValuePair<ClashResult, string>>>> blocks)
        {
            var payload = new Dictionary<string, object>
            {
                { "document", doc.Title ?? "" },
                { "generated", DateTime.Now },
                { "generator", "AutoNAV MCP" },
                { "tests", blocks.Select(block => (object)new Dictionary<string, object>
                    {
                        { "name", block.Key.DisplayName ?? "" },
                        { "status", block.Key.Status.ToString() },
                        { "lastRun", block.Key.LastRun },
                        { "resultCounts", ClashHelpers.CountResultsByStatus(block.Key) },
                        { "clashes", block.Value.Select(p =>
                            (object)ClashHelpers.SerializeResult(doc, p.Key, p.Value, true)).ToList() },
                    }).ToList() },
            };
            File.WriteAllText(path, MiniJson.Serialize(payload), new UTF8Encoding(false));
        }

        // ── HTML ─────────────────────────────────────────────────────

        private static void WriteHtml(Document doc, DocumentClash clash, string path,
            List<KeyValuePair<ClashTest, List<KeyValuePair<ClashResult, string>>>> blocks,
            bool includeImages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Clash Report — " + Html(doc.Title) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1a1a1a}");
            sb.AppendLine("h1{font-size:22px}h2{font-size:17px;margin-top:28px;border-bottom:2px solid #444;padding-bottom:4px}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:12px;margin-top:8px}");
            sb.AppendLine("th,td{border:1px solid #ccc;padding:4px 7px;text-align:left;vertical-align:top}");
            sb.AppendLine("th{background:#f0f0f0}");
            sb.AppendLine(".s-New{color:#c00;font-weight:600}.s-Active{color:#d60}.s-Reviewed{color:#06c}");
            sb.AppendLine(".s-Approved{color:#080}.s-Resolved{color:#555}");
            sb.AppendLine("img.clash{max-width:220px;border:1px solid #ccc}");
            sb.AppendLine(".meta{color:#555;font-size:13px}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<h1>Clash Report — " + Html(doc.Title) + "</h1>");
            sb.AppendLine("<p class=\"meta\">Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") +
                          " by AutoNAV MCP · " + blocks.Count + " test(s)</p>");

            // Summary table.
            sb.AppendLine("<h2>Summary</h2><table><tr><th>Test</th><th>Status</th><th>Last Run</th><th>New</th><th>Active</th><th>Reviewed</th><th>Approved</th><th>Resolved</th><th>Total</th></tr>");
            foreach (var block in blocks)
            {
                var counts = ClashHelpers.CountResultsByStatus(block.Key);
                sb.AppendLine("<tr><td>" + Html(block.Key.DisplayName) + "</td><td>" + block.Key.Status +
                    "</td><td>" + (block.Key.LastRun.HasValue ? block.Key.LastRun.Value.ToString("yyyy-MM-dd HH:mm") : "never") + "</td>" +
                    Cell(counts, "New") + Cell(counts, "Active") + Cell(counts, "Reviewed") +
                    Cell(counts, "Approved") + Cell(counts, "Resolved") + Cell(counts, "Total") + "</tr>");
            }
            sb.AppendLine("</table>");

            foreach (var block in blocks)
            {
                sb.AppendLine("<h2>" + Html(block.Key.DisplayName) + " (" + block.Value.Count + " clash(es))</h2>");
                if (block.Value.Count == 0) { sb.AppendLine("<p class=\"meta\">No matching clashes.</p>"); continue; }
                sb.AppendLine("<table><tr>" + (includeImages ? "<th>Image</th>" : "") +
                    "<th>Clash</th><th>Group</th><th>Status</th><th>Distance</th><th>Grid Location</th><th>Assigned To</th><th>Item 1</th><th>Item 2</th><th>Comments</th></tr>");
                foreach (var pair in block.Value)
                {
                    ClashResult r = pair.Key;
                    ModelItem i1 = r.CompositeItem1 ?? r.Item1;
                    ModelItem i2 = r.CompositeItem2 ?? r.Item2;
                    sb.Append("<tr>");
                    if (includeImages)
                    {
                        string b64 = TryGetImageBase64(clash, r);
                        sb.Append("<td>" + (b64 != null
                            ? "<img class=\"clash\" src=\"data:image/png;base64," + b64 + "\">"
                            : "&mdash;") + "</td>");
                    }
                    sb.Append("<td>" + Html(r.DisplayName) + "</td>");
                    sb.Append("<td>" + Html(pair.Value) + "</td>");
                    sb.Append("<td class=\"s-" + r.Status + "\">" + r.Status + "</td>");
                    sb.Append("<td>" + r.Distance.ToString("0.###", CultureInfo.InvariantCulture) + "</td>");
                    sb.Append("<td>" + Html(ClashHelpers.DescribeGridLocation(doc, r.Center)) + "</td>");
                    sb.Append("<td>" + Html(ClashCompat.GetAssignedTo(r)) + "</td>");
                    sb.Append("<td>" + Html(ClashHelpers.DescribeItem(i1)) + "<br><span class=\"meta\">" + Html(ClashHelpers.GetFileAncestorName(i1)) + "</span></td>");
                    sb.Append("<td>" + Html(ClashHelpers.DescribeItem(i2)) + "<br><span class=\"meta\">" + Html(ClashHelpers.GetFileAncestorName(i2)) + "</span></td>");
                    sb.Append("<td>" + Html(JoinComments(r)) + "</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</body></html>");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string Cell(Dictionary<string, object> counts, string key)
        {
            object v;
            counts.TryGetValue(key, out v);
            return "<td>" + (v ?? 0) + "</td>";
        }

        private static string JoinComments(ClashResult result)
        {
            if (result.Comments == null || result.Comments.Count == 0) return "";
            var parts = new List<string>();
            foreach (Comment comment in result.Comments)
            {
                string author = ClashCompat.GetCommentAuthor(comment);
                parts.Add((string.IsNullOrEmpty(author) ? "" : author + ": ") + (comment.Body ?? ""));
            }
            return string.Join(" | ", parts);
        }

        private static string Html(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // TestsImageForResult's signature varies across releases, so it is
        // located via reflection; when unavailable the report simply omits
        // the image column entry.
        private static string TryGetImageBase64(DocumentClash clash, ClashResult result)
        {
            try
            {
                MethodInfo best = null;
                foreach (MethodInfo m in clash.TestsData.GetType().GetMethods())
                {
                    if (m.Name != "TestsImageForResult") continue;
                    if (best == null || m.GetParameters().Length < best.GetParameters().Length) best = m;
                }
                if (best == null) return null;

                ParameterInfo[] parameters = best.GetParameters();
                var invokeArgs = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type pt = parameters[i].ParameterType;
                    if (typeof(ClashResult).IsAssignableFrom(pt) || pt.Name == "IClashResult")
                        invokeArgs[i] = result;
                    else if (pt == typeof(int))
                        invokeArgs[i] = 320;
                    else if (pt.IsEnum)
                        invokeArgs[i] = Enum.GetValues(pt).GetValue(0);
                    else if (pt.IsValueType)
                        invokeArgs[i] = Activator.CreateInstance(pt);
                    else
                        invokeArgs[i] = null;
                }

                var image = best.Invoke(clash.TestsData, invokeArgs) as System.Drawing.Image;
                if (image == null) return null;
                using (image)
                using (var ms = new MemoryStream())
                {
                    image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
