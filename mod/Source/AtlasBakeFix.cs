using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Removes static-atlas tiles whose texture never made it into the page.
    ///
    /// The defect: <c>Verse.StaticTextureAtlas.Bake</c> registers a tile for every texture before it
    /// draws anything - <c>BuildMeshesForUvs</c> writes texture->uvRect into <c>tiles</c>
    /// (static.cs:164-171) - and then blits each texture into a page that was cleared to
    /// <c>Color.clear</c> (BlitTexturesToColorAtlas, static.cs:474-492). The blit's only gate on the
    /// source is <c>texture.GpuTexture != null</c> after an <c>EnsureGpuResident()</c> attempt
    /// (UnityStubs Graphics.cs, TryGetGpuTexture); when that fails <c>Graphics.DrawTexture</c> calls
    /// <c>StubWarner.LogStub</c> and returns - no exception, no error, no cell written. The bake runs
    /// inside the deferred-loading window (PlayDataLoader.DoPlayLoad -> BeginDeferredLoading ->
    /// BakeStaticAtlases), where a texture that cannot be hydrated from its source or from the texture
    /// cache is exactly this case.
    ///
    /// Afterwards <c>Graphic.TryGetTextureAtlasReplacementInfo</c> finds the tile, swaps the material's
    /// main texture for the atlas page and the uvRect for the empty cell, and returns true - so the
    /// graphic stops using the texture it does have, forever. One blank cell, permanent.
    ///
    /// The fix is a postfix on Bake that measures the same predicate the blit gated on and unregisters
    /// the tiles that failed:
    ///   * <c>AdoptedFromCache</c> -> do nothing at all. The page came from a cache keyed by every
    ///     source stamp and uvRect; no blit ran and residency is irrelevant. Returning here is what
    ///     keeps a warm start from uploading (and then throwing away) every source texture.
    ///   * page not resident -> the whole page is unusable; unregister every tile.
    ///   * colour not resident -> unregister that tile.
    ///   * mask not resident (masked atlas) -> unregister that tile: the replacement binds the whole
    ///     mask page, and a zero mask texel multiplies the icon's colour away, i.e. a black icon,
    ///     which is worse than the untinted source texture it falls back to.
    /// The source texture is then simply not put in the atlas - it is drawn the way it would have been
    /// drawn without an atlas at all, and nothing in the engine is left pointing at an empty cell.
    ///
    /// A prefix that drops the texture *before* the blit was rejected: Bake decides
    /// <c>AdoptedFromCache</c> only after CalcRects/BuildMeshesForUvs (static.cs:180-190), so a prefix
    /// cannot know yet whether a blit will happen, and forcing EnsureGpuResident() on every source
    /// before that decision would defeat the page cache on every warm start (uploading hundreds of
    /// textures that the adopted page makes unnecessary).
    ///
    /// Every reflection member is resolved once at install; any missing member, patch rejection or
    /// exception inside the postfix leaves the atlas exactly as the engine baked it. Nothing here
    /// creates, moves or destroys a texture - it only unregisters dictionary entries.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class AtlasBakeFix
    {
        /// <summary>
        /// Self-install. This module does not depend on Bootstrap: StaticConstructorOnStartupUtility
        /// runs every attributed type at PlayDataLoader.DoPlayLoad:35398, which is before
        /// GlobalTextureAtlasManager.BakeStaticAtlases() at :35413 - i.e. before the first bake this
        /// patch has to see. Keeping the wiring here also means the module touches one file only.
        /// The static ctor is idempotent, so an explicit Install() from Bootstrap is harmless.
        /// </summary>
        static AtlasBakeFix()
        {
            try
            {
                Init();
                Install();
            }
            catch (Exception e) { WarnOnce("static ctor: " + e.Message); }
        }

        /// <summary>Same file-switch convention as the rest of the mod: contents "off" disables.</summary>
        internal const string SwitchPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.atlasbake";

        private const string HarmonyId = "sleepsheep.waylith.agenttouchfix.atlasbake";
        private const int DetailCap = 8;

        private static bool _off, _resolved, _installed;
        private static int _bakes, _adopted, _dropped, _detailLogged, _warned;

        private static Type _atlasType, _texType;
        private static FieldInfo _fTextures, _fMasks, _fTiles, _fColorKey, _fMaskKey, _fGroupKey;
        private static PropertyInfo _pAdopted, _pColorPage, _pMaskPage, _pCore, _pName;
        private static MethodInfo _mEnsureResident, _mFinishPendingBuild;
        private static FieldInfo _fReplacementCache;   // Verse.Graphic.replacementInfoCache (static)
        private static FieldInfo _fHasMask;            // TextureAtlasGroupKey.hasMask

        internal static void Init()
        {
            try
            {
                string text = "";
                try
                {
                    if (File.Exists(SwitchPath)) text = File.ReadAllText(SwitchPath);
                }
                catch (Exception e) { Refl.Warn("atlasbake: switch unreadable: " + e.Message); }

                _off = !AtlasBakeDecision.Enabled(text);
                if (_off) Refl.Info("atlasbake: disabled by " + SwitchPath);
            }
            catch (Exception e) { WarnOnce("init: " + e.Message); }
        }

        internal static void Install()
        {
            if (_off || _installed) return;
            try
            {
                _atlasType = Refl.T("Verse.StaticTextureAtlas");
                _texType = Refl.T("UnityEngine.Texture2D");
                if (_atlasType == null || _texType == null)
                {
                    Refl.Warn("atlasbake: types missing (atlas=" + (_atlasType != null)
                              + " tex2d=" + (_texType != null) + "), skipped");
                    return;
                }

                MethodInfo bake = _atlasType.GetMethod("Bake", BindingFlags.Public | BindingFlags.Instance,
                                                       null, new[] { typeof(bool) }, null);
                if (bake == null)
                {
                    Refl.Warn("atlasbake: StaticTextureAtlas.Bake(Boolean) not found, skipped");
                    return;
                }

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fTextures = _atlasType.GetField("textures", any);
                _fMasks = _atlasType.GetField("masks", any);
                _fTiles = _atlasType.GetField("tiles", any);
                _fColorKey = _atlasType.GetField("colorCacheKey", any);
                _fMaskKey = _atlasType.GetField("maskCacheKey", any);
                _fGroupKey = _atlasType.GetField("groupKey", any);
                _pAdopted = Prop(_atlasType, "AdoptedFromCache");
                _pColorPage = Prop(_atlasType, "ColorTexture") ?? Prop(_atlasType, "colorTexture");
                _pMaskPage = Prop(_atlasType, "MaskTexture") ?? Prop(_atlasType, "maskTexture");
                _mEnsureResident = _texType.GetMethod("EnsureGpuResident", BindingFlags.Public | BindingFlags.Instance);
                _pCore = Prop(_texType, "CoreTexture");
                _pName = Prop(_texType, "name");
                if (_pCore != null)
                {
                    Type core = _pCore.PropertyType;
                    _mFinishPendingBuild = core.GetMethod("FinishPendingBuild", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_fTextures == null || _fMasks == null || _fTiles == null || _pAdopted == null
                    || _pColorPage == null || _mEnsureResident == null)
                {
                    Refl.Warn("atlasbake: members not reachable (textures=" + (_fTextures != null)
                              + " masks=" + (_fMasks != null) + " tiles=" + (_fTiles != null)
                              + " adopted=" + (_pAdopted != null) + " page=" + (_pColorPage != null)
                              + " ensure=" + (_mEnsureResident != null) + "), skipped");
                    return;
                }

                if (_fGroupKey != null) _fHasMask = _fGroupKey.FieldType.GetField("hasMask", any);

                Type graphic = Refl.T("Verse.Graphic");
                _fReplacementCache = graphic?.GetField("replacementInfoCache",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                MethodInfo postfix = typeof(AtlasBakeFix).GetMethod(nameof(AfterBake),
                    BindingFlags.Static | BindingFlags.NonPublic);
                var hm = new HarmonyMethod(postfix) { priority = Priority.Low };
                new Harmony(HarmonyId).Patch(bake, postfix: hm);

                _resolved = true;
                _installed = true;
                Refl.Info("atlasbake: postfix armed on Verse.StaticTextureAtlas.Bake(Boolean)"
                          + " (priority Low, " + _atlasType.FullName + ")"
                          + " | ensure=" + _mEnsureResident.DeclaringType.FullName
                          + " finishPending=" + (_mFinishPendingBuild != null)
                          + " replacementCache=" + (_fReplacementCache != null)
                          + " hasMask=" + (_fHasMask != null));
            }
            catch (Exception e)
            {
                Refl.Warn("atlasbake: install failed: " + e.Message);
            }
        }

        // --------------------------------------------------------------------- postfix

        private static void AfterBake(object __instance)
        {
            try
            {
                if (_off || !_resolved || __instance == null) return;

                IList textures = _fTextures.GetValue(__instance) as IList;
                IDictionary tiles = _fTiles.GetValue(__instance) as IDictionary;
                if (textures == null || tiles == null || textures.Count == 0) return;

                // A page that came out of the texture cache needs no blit and no residency: its tiles
                // were laid out with the same source stamps and uvRects that the cache key hashes.
                if (ReadBool(_pAdopted, __instance))
                {
                    _adopted++;
                    return;
                }

                IDictionary masks = _fMasks.GetValue(__instance) as IDictionary;
                bool masked = ReadHasMask(__instance) || (masks != null && masks.Count > 0);
                bool pageOk = Resident(_pColorPage.GetValue(__instance, null));
                bool maskPageOk = !masked || Resident(_pMaskPage?.GetValue(__instance, null));

                var doomed = new List<object>();
                var reasons = new List<string>();
                int kept = 0;
                for (int i = 0; i < textures.Count; i++)
                {
                    object tex = textures[i];
                    if (tex == null) continue;
                    bool hasMask = masks != null && masks.Contains(tex);
                    bool colorOk = Resident(tex);
                    bool maskOk = !hasMask || Resident(masks[tex]);

                    AtlasBakeAction action = AtlasBakeDecision.Decide(false, pageOk && maskPageOk, colorOk,
                        hasMask, maskOk);
                    if (action == AtlasBakeAction.Keep) { kept++; continue; }
                    doomed.Add(tex);
                    reasons.Add(Describe(tex) + " " + AtlasBakeDecision.Reason(action));
                }

                int n = _bakes + 1;
                if (doomed.Count == 0)
                {
                    _bakes = n;
                    if (n <= 3)
                        Refl.Info("atlasbake: bake " + n + " tiles=" + tiles.Count
                                  + " page=" + (pageOk ? "resident" : "MISSING") + " kept=" + kept);
                    return;
                }

                // Unregister in all three collections so the atlas stays self-consistent: textures and
                // tiles keep the same count (TryGetCacheKey checks that), Members stops advertising the
                // texture as atlas-evictable, and no tile is left pointing at a cell nobody wrote.
                for (int i = 0; i < doomed.Count; i++)
                {
                    object tex = doomed[i];
                    tiles.Remove(tex);
                    textures.Remove(tex);
                    masks?.Remove(tex);
                }
                // A page with holes must never be persisted or adopted under a key that claims it is
                // complete; CompressAtlasPage still compresses, it just does not write a cache entry.
                SetNull(_fColorKey, __instance);
                SetNull(_fMaskKey, __instance);
                if (_fReplacementCache?.GetValue(null) is IDictionary cache) cache.Clear();
                _dropped += doomed.Count;
                _bakes = n;

                Refl.Info("atlasbake: bake " + n + " tiles=" + tiles.Count + " kept=" + kept
                          + " dropped=" + doomed.Count + " page=" + (pageOk ? "resident" : "MISSING")
                          + " maskPage=" + (masked ? (maskPageOk ? "resident" : "MISSING") : "none"));
                for (int i = 0; i < reasons.Count && _detailLogged < DetailCap; i++)
                {
                    _detailLogged++;
                    Refl.Info("atlasbake: drop " + reasons[i]);
                }
                if (reasons.Count > DetailCap && _detailLogged >= DetailCap)
                    Refl.Info("atlasbake: (" + (reasons.Count - DetailCap) + " more drops not listed)");
            }
            catch (Exception e)
            {
                WarnOnce("postfix: " + e.Message);
            }
        }

        // --------------------------------------------------------------------- helpers

        /// <summary>
        /// <c>Texture2D.EnsureGpuResident()</c>, with one retry after finishing a build that was still
        /// pending: <c>EnsureResident</c> answers false while <c>PendingBuild != null</c> and only the
        /// second call after <c>FinishPendingBuild()</c> tells the truth. The retry is what a texture
        /// that is still uploading at bake time needs in order not to be dropped by mistake.
        /// </summary>
        private static bool Resident(object texture)
        {
            if (texture == null) return false;
            try
            {
                if (_mEnsureResident.Invoke(texture, null) is bool ok && ok) return true;
                object core = _pCore?.GetValue(texture, null);
                if (core == null || _mFinishPendingBuild == null) return false;
                _mFinishPendingBuild.Invoke(core, null);
                return _mEnsureResident.Invoke(texture, null) is bool again && again;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// First property with this name, hiding and all. <c>GetProperty(name)</c> throws
        /// AmbiguousMatchException on this port: <c>UnityEngine.Texture2D</c> declares
        /// <c>new CoreTexture</c> over <c>Texture.CoreTexture</c>, so a plain lookup never resolves
        /// and the whole install would be refused (measured: "install failed: Ambiguous match found
        /// for 'UnityEngine.Texture2D Waylith.Rendering.Texture2D CoreTexture'").
        /// </summary>
        private static PropertyInfo Prop(Type t, string name)
        {
            if (t == null) return null;
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                                       BindingFlags.Instance | BindingFlags.Static))
            {
                if (p.Name == name && p.GetIndexParameters().Length == 0) return p;
            }
            return null;
        }

        private static bool ReadBool(PropertyInfo p, object instance)
        {
            try { return p != null && p.GetValue(instance, null) is bool b && b; }
            catch { return false; }
        }

        private static bool ReadHasMask(object atlas)
        {
            try
            {
                if (_fGroupKey == null || _fHasMask == null) return false;
                object key = _fGroupKey.GetValue(atlas);
                return key != null && _fHasMask.GetValue(key) is bool b && b;
            }
            catch { return false; }
        }

        private static void SetNull(FieldInfo f, object instance)
        {
            try { f?.SetValue(instance, null); } catch { }
        }

        private static string Describe(object texture)
        {
            try
            {
                string name = _pName?.GetValue(texture, null) as string;
                return "'" + (string.IsNullOrEmpty(name) ? "?" : name) + "'";
            }
            catch { return "'?'"; }
        }

        private static void WarnOnce(string message)
        {
            if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
                Refl.Warn("atlasbake: " + message);
        }

        /// <summary>Read by the startup line so a log shows whether anything was measured at all.</summary>
        internal static string Summary()
        {
            return "atlasbake bakes=" + _bakes + " adopted=" + _adopted + " dropped=" + _dropped
                   + (_off ? " (off)" : _resolved ? "" : " (not armed)");
        }
    }
}
