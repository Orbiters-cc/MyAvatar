using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.MyAvatar.Editor.Gallery;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.MCP
{
    /// <summary>
    /// Creating and publishing My Avatar gallery assets through MCP, like mcb_authoring for custom bases. It edits the same
    /// draft as the "Publish to the gallery" window, builds and tests packages with the same rules, and publishes or
    /// withdraws only with a code the user approved.
    /// </summary>
    [McpForUnityTool("myavatar_gallery", Description = "Create and publish My Avatar gallery assets (clothes, accessories). inspect shows the draft shared with the 'Publish to the gallery' window, scene avatars (target_id) and the API it publishes to. configure merges data into that draft (draftSchema from inspect; variants take prefabPaths or packagePath, and manifest setup prefabs by path; reset:true starts over). list_assets reads the creator's assets and avatar bases. build packages every variant (or `variant`) with the packaging rules; test installs one built variant on target_id. Publishing needs preview_publish, then explicit human approval of the shown summary and rights statement, then confirm_publish with the returned code; withdraw likewise needs preview_withdraw and confirm_withdraw. Never infer approval. Network and build actions return pending jobs (poll status).", RequiresPolling = true, PollAction = "status", MaxPollSeconds = 180)]
    public static class GalleryAuthoringTool
    {
        public sealed class Parameters
        {
            [ToolParameter("inspect, configure, list_assets, build, test, preview_publish, confirm_publish, preview_withdraw, confirm_withdraw, status")] public string action { get; set; }
            [ToolParameter("Scene avatar (MyAvatar component or GameObject ID) used to build and test", Required = false)] public int target_id { get; set; }
            [ToolParameter("configure: fields of the draft to change; preview_withdraw: {releaseId}", Required = false)] public object data { get; set; }
            [ToolParameter("build/test: the variant label (default: every variant for build, the first for test)", Required = false)] public string variant { get; set; }
            [ToolParameter("Job ID returned by an operation", Required = false)] public string job_id { get; set; }
            [ToolParameter("Single-use code from a preview; only pass after explicit user approval", Required = false)] public string confirmation_code { get; set; }
        }

        private sealed class Job { public string Id, Action; public Task<object> Work; public double Started; }
        private sealed class Approval { public string Kind, Fingerprint; public int ReleaseId; public double Expires; }
        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        private static readonly Dictionary<string, Approval> Approvals = new Dictionary<string, Approval>();
        private static Job active;
        private static string latest;
        private const string Rights = "I own the rights to everything this version distributes, or I have permission to share it.";

        public static object HandleCommand(JObject arguments)
        {
            try
            {
                var p = arguments?.ToObject<Parameters>() ?? throw new ArgumentException("Parameters are required.");
                var data = p.data as JObject ?? (p.data is string text && !string.IsNullOrWhiteSpace(text) ? JObject.Parse(text) : null);
                switch (p.action)
                {
                    case "status": return Status(p.job_id ?? latest);
                    case "inspect": return new SuccessResponse("My Avatar gallery authoring state.", Inspect());
                }
                if (active != null && !active.Work.IsCompleted) throw new InvalidOperationException("A gallery operation is running: " + active.Id);
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Wait for Unity to be idle in edit mode.");
                var draft = GalleryCreatorDraft.Load();
                switch (p.action)
                {
                    case "configure":
                        Configure(draft, data ?? throw new ArgumentException("Provide the draft fields to change."));
                        return new SuccessResponse("Gallery draft saved; the Publish to the gallery window shows it.", Summary(GalleryCreatorDraft.Load()));
                    case "list_assets":
                        return Start(p.action, async () =>
                        {
                            var mine = await GalleryApi.CreatorAssetsAsync(CancellationToken.None);
                            var bases = await GalleryApi.BasesAsync(CancellationToken.None);
                            return (object)new { mine, bases = bases?.bases.Select(b => new { b.id, b.name }) };
                        });
                    case "build":
                    {
                        var avatar = Avatar(p.target_id, false);
                        var variants = Variants(draft, p.variant);
                        return Start(p.action, async () =>
                        {
                            foreach (var v in variants) await GalleryPublisher.BuildAsync(draft, v, avatar);
                            GalleryPublisher.NotifyDraftReplaced();
                            return (object)new { variants = variants.Select(VariantSummary), missing = GalleryPublisher.Missing(draft) };
                        });
                    }
                    case "test":
                    {
                        var variant = Variants(draft, p.variant).First();
                        var job = GalleryPublisher.Test(draft, variant, Avatar(p.target_id, true));
                        GalleryPublisher.NotifyDraftReplaced();
                        return new SuccessResponse("Test installation started; inspect shows its progress. Questions it asks are answered in My Avatar.", new { installJob = job.id, job.stage, job.status });
                    }
                    case "preview_publish":
                    {
                        string missing = GalleryPublisher.Missing(draft);
                        if (missing != null) throw new InvalidOperationException(missing);
                        string code = NewApproval(new Approval { Kind = "publish", Fingerprint = Fingerprint(draft) });
                        return new SuccessResponse("Awaiting explicit user approval to publish. Show the summary and the rights statement; only confirm_publish once the user agrees.",
                            new { confirmation_code = code, publishesTo = global::OrbitersEnvironment.ApiUrl(), rightsStatement = Rights, draft = Summary(draft), expires_in_seconds = 900 });
                    }
                    case "confirm_publish":
                    {
                        var approval = Take(p.confirmation_code, "publish");
                        if (Fingerprint(draft) != approval.Fingerprint) throw new InvalidOperationException("The draft changed since the preview; request a new preview and approval.");
                        draft.rights = true; draft.Save();
                        return Start(p.action, async () =>
                        {
                            var published = await GalleryPublisher.PublishAsync(draft, null, CancellationToken.None);
                            GalleryPublisher.NotifyDraftReplaced();
                            return (object)new { assetId = published.id, version = published.version, listed = published.listed };
                        });
                    }
                    case "preview_withdraw":
                    {
                        int releaseId = data?.Value<int?>("releaseId") ?? throw new ArgumentException("Provide data.releaseId (list_assets shows each asset's releases).");
                        string code = NewApproval(new Approval { Kind = "withdraw", ReleaseId = releaseId });
                        return new SuccessResponse("Awaiting explicit user approval to withdraw this version from the gallery (installed copies keep working).",
                            new { confirmation_code = code, releaseId, withdrawsFrom = global::OrbitersEnvironment.ApiUrl(), expires_in_seconds = 900 });
                    }
                    case "confirm_withdraw":
                    {
                        var approval = Take(p.confirmation_code, "withdraw");
                        return Start(p.action, async () => { await GalleryApi.WithdrawReleaseAsync(approval.ReleaseId); GalleryCache.Clear(); return (object)new { withdrawn = approval.ReleaseId }; });
                    }
                    default: throw new ArgumentException("Unknown gallery action.");
                }
            }
            catch (Exception ex) { return new ErrorResponse(ex.Message); }
        }

        private static object Inspect()
        {
            var draft = GalleryCreatorDraft.Load();
            return new
            {
                publishesTo = global::OrbitersEnvironment.ApiUrl(),
                signedIn = !string.IsNullOrEmpty(global::AuthenticationService.GetAuth()?.token),
                avatars = Resources.FindObjectsOfTypeAll<MyAvatar>().Where(a => a.gameObject.scene.IsValid()).Select(a => new { target_id = a.GetInstanceID(), avatar = a.name }).ToArray(),
                draft = Summary(draft),
                missing = GalleryPublisher.Missing(draft),
                tests = draft.variants.Where(v => !string.IsNullOrEmpty(v.testJob)).Select(v => new { v.label, job = GalleryJournal.All.FirstOrDefault(x => x.id == v.testJob) is GalleryJob j ? new { j.stage, j.status, j.waiting } : null }),
                draftSchema = JObject.FromObject(new GalleryCreatorDraft()),
                variantSchema = new { label = "PC", source = "prefab | package", prefabPaths = new[] { "Assets/Example/Item.prefab" }, packagePath = "C:/path/item.unitypackage",
                    platforms = new[] { "pc", "android", "ios" }, baseScope = "any | bases", baseIds = new int[0] },
            };
        }

        // Fields the user decides through approval or the window only.
        private static readonly string[] Protected = { "rights", "assetId", "releaseId", "idempotencyKey", "message", "messageError" };

        private static void Configure(GalleryCreatorDraft draft, JObject data)
        {
            if (data.Value<bool?>("reset") == true) { GalleryCreatorDraft.Clear(); draft = GalleryCreatorDraft.Load(); }
            data = (JObject)data.DeepClone();
            data.Remove("reset");
            foreach (string key in Protected) data.Remove(key);
            if (data["existingAssetId"] != null) { draft.assetId = data.Value<int>("existingAssetId"); draft.newAsset = draft.assetId <= 0; data.Remove("existingAssetId"); }
            if (data["variants"] is JArray variants)
                foreach (var variant in variants.OfType<JObject>())
                {
                    if (variant["prefabPaths"] is JArray paths)
                    {
                        variant["prefabGuids"] = new JArray(paths.Select(path =>
                        {
                            string guid = AssetDatabase.AssetPathToGUID((string)path);
                            if (string.IsNullOrEmpty(guid) || AssetDatabase.LoadAssetAtPath<GameObject>((string)path) == null) throw new ArgumentException("Not a prefab in this project: " + path);
                            return guid;
                        }));
                        variant.Remove("prefabPaths");
                        if (variant["source"] == null) variant["source"] = "prefab";
                    }
                    if (variant["packagePath"] != null && variant["source"] == null) variant["source"] = "package";
                    // Setup prefabs are named by path; their GUID comes from the project.
                    foreach (var prefab in (variant["manifest"]?["setups"] as JArray ?? new JArray()).OfType<JObject>().SelectMany(s => (s["prefabs"] as JArray ?? new JArray()).OfType<JObject>()))
                    {
                        string path = (string)prefab["path"];
                        if (string.IsNullOrEmpty(path)) continue;
                        string guid = AssetDatabase.AssetPathToGUID(path);
                        if (string.IsNullOrEmpty(guid)) throw new ArgumentException("Not an asset in this project: " + path);
                        prefab["guid"] = guid;
                        if (prefab["name"] == null) prefab["name"] = System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                    variant.Remove("report"); variant.Remove("uploadedId"); variant.Remove("uploadedSha"); variant.Remove("testJob");
                }
            JsonConvert.PopulateObject(data.ToString(), draft, new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
            draft.rights = false;
            draft.Save();
            GalleryPublisher.NotifyDraftReplaced();
        }

        private static MyAvatar Avatar(int id, bool required)
        {
            if (id == 0)
            {
                if (required) throw new ArgumentException("Provide target_id (inspect lists scene avatars).");
                return null;
            }
            var found = EditorUtility.InstanceIDToObject(id);
            var avatar = found as MyAvatar ?? (found as GameObject)?.GetComponent<MyAvatar>();
            if (avatar == null || !avatar.gameObject.scene.IsValid()) throw new ArgumentException("target_id is not a scene avatar with My Avatar.");
            return avatar;
        }

        private static List<GalleryVariantDraft> Variants(GalleryCreatorDraft draft, string label)
        {
            var list = string.IsNullOrEmpty(label) ? draft.variants : draft.variants.Where(v => string.Equals(v.label, label, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 0) throw new ArgumentException(string.IsNullOrEmpty(label) ? "The draft has no variant." : "No variant is labelled " + label + ".");
            return list;
        }

        private static object Summary(GalleryCreatorDraft d) => new
        {
            asset = d.assetId > 0 ? (object)new { existingAssetId = d.assetId } : new { d.name, d.type, d.shortDescription, d.description, d.free, d.priceCents, d.currency, d.gumroad, d.jinxxy, d.preferredStore, d.thumbnailPath },
            version = new { d.version, d.title, d.changelog, d.scope, d.releaseId },
            d.publishListing,
            variants = d.variants.Select(VariantSummary),
        };

        private static object VariantSummary(GalleryVariantDraft v) => new
        {
            v.label, v.source, prefabs = v.prefabGuids.Select(AssetDatabase.GUIDToAssetPath), v.packagePath, v.platforms, v.baseScope, v.baseIds,
            setups = v.manifest.setups.Select(s => new { s.key, s.label, prefabs = s.prefabs.Select(x => new { x.path, attach = x.attach?.mode, x.attach?.bone }) }),
            built = v.report == null ? null : new
            {
                publishable = v.report.Publishable, v.report.bytes, v.report.sha256, v.report.parameterBits, v.report.containsCode, v.report.codeFiles,
                v.report.errors, v.report.warnings, v.report.excluded, dependencies = v.report.dependencies.Select(x => x.id + " " + x.range),
            },
            uploaded = v.uploadedId > 0 && v.report != null && v.uploadedSha == v.report.sha256,
        };

        private static string Fingerprint(GalleryCreatorDraft draft)
        {
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Summary(draft)))));
        }

        private static string NewApproval(Approval approval)
        {
            string code = Guid.NewGuid().ToString("N");
            approval.Expires = EditorApplication.timeSinceStartup + 900;
            Approvals.Clear(); Approvals.Add(code, approval);
            return code;
        }

        private static Approval Take(string code, string kind)
        {
            if (code == null || !Approvals.TryGetValue(code, out var approval) || approval.Kind != kind || approval.Expires < EditorApplication.timeSinceStartup)
                throw new InvalidOperationException("Request a new preview and obtain the user's approval first.");
            Approvals.Remove(code);
            return approval;
        }

        private static object Start(string action, Func<Task<object>> work)
        {
            foreach (string id in Jobs.Where(j => j.Value.Work.IsCompleted).Select(j => j.Key).ToArray()) if (Jobs.Count >= 16) Jobs.Remove(id);
            var job = new Job { Id = Guid.NewGuid().ToString("N"), Action = action, Started = EditorApplication.timeSinceStartup, Work = work() };
            Jobs.Add(job.Id, job); active = job; latest = job.Id;
            return Status(job.Id);
        }

        private static object Status(string id)
        {
            if (id == null || !Jobs.TryGetValue(id, out var job)) return new ErrorResponse("Job unavailable (a script reload ends running jobs). inspect shows the draft; publishing resumes where it stopped.");
            if (!job.Work.IsCompleted) return new PendingResponse("Gallery " + job.Action + " is running.", 1, new { job_id = job.Id, elapsed_seconds = EditorApplication.timeSinceStartup - job.Started });
            if (job.Work.IsFaulted) return new ErrorResponse(job.Action + " failed: " + (job.Work.Exception?.GetBaseException().Message ?? "unknown error"));
            return new SuccessResponse(job.Action + " completed.", job.Work.Result);
        }
    }
}
