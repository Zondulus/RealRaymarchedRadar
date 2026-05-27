using Atmosphere;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KSP.UI.Screens;

namespace KerbalWeatherRadar
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class WeatherRadarController : MonoBehaviour
    {
        // --- Configuration ---
        private float shortRange = 10000f;
        private float longRange = 20000f;
        private float lowRangeAngle = 10f;
        private float highRangeAngle = 5f;
        private float radarThreshold = 0.01f;
        private float baseColorMultiplier = 1.5f;
        private int radarSteps = 50;
        private float sweepSpeedShort = 120f;
        private float sweepSpeedLong = 60f;
        private float sweepResolution = 0.5f;

        // --- UI & AppLauncher ---
        private ApplicationLauncherButton appLauncherButton;
        private bool showUI = false;
        private bool uiHidden = false; // F2 UI Toggle state
        private Rect windowRect = new Rect(200, 200, 280, 20);
        private GUIStyle richButtonStyle;

        // --- Radar Display ---
        private Texture2D radarTexture;
        private const int TEX_SIZE = 256;
        private const int TEX_CENTER = 128;
        private Color32[] clearColors;
        private Color32[] pixelBuffer;
        private int[] overlayIndices;
        private Color32 overlayColor = new Color32(51, 51, 51, 255);

        // --- Radar State ---
        private bool isGlobalMode = true;
        private bool isLongRange = false;
        private float cutoffHorizAlt = 2000f;
        private float cutoffDownAlt = 3000f;

        private int gainState = 0; // 0 = Med, 1 = Hi, 2 = Lo
        private float currentColorMultiplier = 1.5f;

        private float currentSweepAngle = 0f;
        private float lastDrawAngle = 0f;
        private float sweepDirection = 1f;
        private float lastUpdateTimer = 0f;

        // --- Caches & Memory Optimization ---
        private float[] densitiesBuffer;
        private List<CloudsRaymarchedVolume> cachedVolumes = new List<CloudsRaymarchedVolume>();
        private float volumeRefreshTimer = 0f;

        public void Start()
        {
            LoadConfig();
            currentColorMultiplier = baseColorMultiplier;

            // Prevent division by zero if configured poorly
            sweepResolution = Mathf.Max(0.1f, sweepResolution);

            // 1. Initialize the Radar Texture and Pixel Buffers using high-perf Color32
            radarTexture = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGBA32, false);
            clearColors = new Color32[TEX_SIZE * TEX_SIZE];
            pixelBuffer = new Color32[TEX_SIZE * TEX_SIZE];

            for (int i = 0; i < clearColors.Length; i++)
            {
                clearColors[i] = new Color32(0, 0, 0, 255);
                pixelBuffer[i] = new Color32(0, 0, 0, 255);
            }

            radarTexture.SetPixels32(pixelBuffer);
            radarTexture.Apply();

            // 2. Generate Static Range Rings & Allocate Buffers
            GenerateOverlayIndices();
            densitiesBuffer = new float[radarSteps];
            UpdateVolumesList(); // Initial population

            // 3. Hook into Toolbar & GameEvents
            GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveToolbarButton);
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        public void OnDestroy()
        {
            // Shut off operations to safely tear down caches
            showUI = false;

            GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveToolbarButton);
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);

            RemoveToolbarButton();

            if (radarTexture != null)
            {
                Destroy(radarTexture);
                radarTexture = null;
            }

            cachedVolumes.Clear();
        }

        // --- Event Listeners ---
        private void OnHideUI() { uiHidden = true; }
        private void OnShowUI() { uiHidden = false; }
        private void OnSceneChange(GameScenes scene)
        {
            // Avoid CTD by halting loops & dropping native volume objects safely before the scene is destroyed 
            showUI = false;
            cachedVolumes.Clear();
        }

        private void LoadConfig()
        {
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("WEATHER_RADAR_CONFIG");
            if (nodes != null && nodes.Length > 0)
            {
                ConfigNode n = nodes[0];
                TryParseFloat(n, "shortRange", ref shortRange);
                TryParseFloat(n, "longRange", ref longRange);
                TryParseFloat(n, "lowRangeAngle", ref lowRangeAngle);
                TryParseFloat(n, "highRangeAngle", ref highRangeAngle);
                TryParseFloat(n, "radarThreshold", ref radarThreshold);
                TryParseFloat(n, "colorMultiplier", ref baseColorMultiplier);
                TryParseInt(n, "radarSteps", ref radarSteps);
                TryParseFloat(n, "sweepSpeedShort", ref sweepSpeedShort);
                TryParseFloat(n, "sweepSpeedLong", ref sweepSpeedLong);
                TryParseFloat(n, "sweepResolution", ref sweepResolution);
                TryParseFloat(n, "cutoffHorizAlt", ref cutoffHorizAlt);
                TryParseFloat(n, "cutoffDownAlt", ref cutoffDownAlt);

                Debug.Log($"[KerbalWeatherRadar] Config loaded. Ranges: {shortRange}/{longRange}m. Angles: {lowRangeAngle}/{highRangeAngle}deg.");
            }
        }

        private static void TryParseFloat(ConfigNode n, string key, ref float field) { if (n.HasValue(key)) float.TryParse(n.GetValue(key), out field); }
        private static void TryParseInt(ConfigNode n, string key, ref int field) { if (n.HasValue(key)) int.TryParse(n.GetValue(key), out field); }

        private void AddToolbarButton()
        {
            if (appLauncherButton == null)
            {
                Texture2D buttonTex = GameDatabase.Instance.GetTexture("KerbalWeatherRadar/Icons/radar_icon", false);
                appLauncherButton = ApplicationLauncher.Instance.AddModApplication(
                    OnToggleTrue, OnToggleFalse, null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT, buttonTex
                );
            }
        }

        private void RemoveToolbarButton()
        {
            // Prevent attempting to access the AppLauncher if it is already destroyed/shutting down
            if (appLauncherButton != null)
            {
                if (ApplicationLauncher.Instance != null)
                {
                    ApplicationLauncher.Instance.RemoveModApplication(appLauncherButton);
                }
                appLauncherButton = null;
            }
        }

        private void OnToggleTrue() { showUI = true; }
        private void OnToggleFalse() { showUI = false; }

        public void Update()
        {
            if (!showUI || !FlightGlobals.ready || FlightDriver.Pause) return;

            // Update Volumes softly in the background to avoid GC spikes
            volumeRefreshTimer -= Time.deltaTime;
            if (volumeRefreshTimer <= 0f)
            {
                UpdateVolumesList();
                volumeRefreshTimer = 2.0f; // Limit to once per 2 seconds
            }

            lastUpdateTimer += Time.deltaTime;
            if (lastUpdateTimer >= 0.033f)
            {
                // Cap dt at 100ms. If the game hangs during loading/GC, the radar will slightly pause instead of drawing a massive chunk all at once.
                float dt = Mathf.Min(lastUpdateTimer, 0.1f);
                float sweepSpeed = isLongRange ? sweepSpeedLong : sweepSpeedShort;

                if (isGlobalMode)
                {
                    currentSweepAngle += sweepSpeed * dt;
                    currentSweepAngle = Mathf.Repeat(currentSweepAngle, 360f);
                }
                else
                {
                    currentSweepAngle += sweepSpeed * sweepDirection * dt;
                    if (currentSweepAngle >= 60f) { currentSweepAngle = 60f; sweepDirection = -1f; }
                    else if (currentSweepAngle <= -60f) { currentSweepAngle = -60f; sweepDirection = 1f; }
                }

                UpdateRadarTexture(sweepSpeed);
                lastUpdateTimer = 0f;
            }
        }

        private void UpdateVolumesList()
        {
            cachedVolumes.Clear();
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            var allClouds = CloudsManager.GetObjectList();
            if (allClouds != null)
            {
                foreach (var layer in allClouds)
                {
                    if (layer.Body == v.mainBody.bodyName && layer.LayerRaymarchedVolume != null)
                    {
                        cachedVolumes.Add(layer.LayerRaymarchedVolume);
                    }
                }
            }
        }

        private void UpdateRadarTexture(float sweepSpeed)
        {
            float sweepTime = 360f / Mathf.Max(1f, sweepSpeed);
            float fadeRate = (1f / sweepTime) * 0.033f;
            byte fadeAmount = (byte)Mathf.Clamp(Mathf.RoundToInt(fadeRate * 255f), 1, 255);

            // High performance integer-based fading logic (No heavy floats)
            for (int i = 0; i < pixelBuffer.Length; i++)
            {
                Color32 p = pixelBuffer[i];
                if (p.r > 0 || p.g > 0 || p.b > 0)
                {
                    int r = p.r - fadeAmount;
                    int g = p.g - fadeAmount;
                    int b = p.b - fadeAmount;
                    p.r = (byte)(r < 0 ? 0 : r);
                    p.g = (byte)(g < 0 ? 0 : g);
                    p.b = (byte)(b < 0 ? 0 : b);
                    pixelBuffer[i] = p;
                }
            }

            float maxRange = isLongRange ? longRange : shortRange;
            float verticalAngle = isLongRange ? highRangeAngle : lowRangeAngle;
            float delta = Mathf.DeltaAngle(lastDrawAngle, currentSweepAngle);

            int stepsToDraw = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(delta) / sweepResolution));
            if (stepsToDraw > 180) stepsToDraw = 1;

            if (stepsToDraw > 0)
            {
                // sample volumes once per drawing phase to remove GC hits
                UpdateDensities(currentSweepAngle, maxRange, verticalAngle, radarSteps);
                for (int i = 1; i <= stepsToDraw; i++)
                {
                    float a = lastDrawAngle + (delta * (i / (float)stepsToDraw));
                    a = Mathf.Repeat(a, 360f);
                    DrawSweepLine(a);
                }
            }

            // Draw Range Rings Over Top
            for (int i = 0; i < overlayIndices.Length; i++)
            {
                pixelBuffer[overlayIndices[i]] = overlayColor;
            }

            lastDrawAngle = currentSweepAngle;
            radarTexture.SetPixels32(pixelBuffer);
            radarTexture.Apply();
        }

        // --- Volumetric Raymarching ---
        private void UpdateDensities(float angleDeg, float maxRange, float verticalAngle, int numSteps)
        {
            Array.Clear(densitiesBuffer, 0, numSteps);

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || cachedVolumes.Count == 0) return;

            Vector3 craftPos = v.transform.position;
            Vector3 center = v.mainBody.transform.position;
            Vector3 up = (craftPos - center).normalized;
            Vector3 nose = v.ReferenceTransform.up;

            Vector3 horizonForward = Vector3.ProjectOnPlane(nose, up);
            if (horizonForward.sqrMagnitude < 0.001f)
            {
                horizonForward = Vector3.ProjectOnPlane(-v.ReferenceTransform.forward, up);
                if (horizonForward.sqrMagnitude < 0.001f)
                    horizonForward = Vector3.ProjectOnPlane(Vector3.forward, up);
            }
            horizonForward = horizonForward.normalized;
            Vector3 horizonRight = Vector3.Cross(up, horizonForward).normalized;

            float angleRad = angleDeg * Mathf.Deg2Rad;
            Vector3 beamHoriz = (horizonForward * Mathf.Cos(angleRad) + horizonRight * Mathf.Sin(angleRad)).normalized;

            // Apply dynamic vertical angle
            float vRad = verticalAngle * Mathf.Deg2Rad;
            Vector3 beam0 = beamHoriz;
            Vector3 beamUp = (beamHoriz * Mathf.Cos(vRad) + up * Mathf.Sin(vRad)).normalized;
            Vector3 beamDown = (beamHoriz * Mathf.Cos(vRad) - up * Mathf.Sin(vRad)).normalized;

            float stepSize = maxRange / numSteps;

            // Don't sample points below sea level.
            float bodyRadiusSq = (float)(v.mainBody.Radius * v.mainBody.Radius);

            // Disable beams near the ground to avoid terrain clipping
            double currentAlt = v.altitude;
            bool enableHoriz = currentAlt >= cutoffHorizAlt;
            bool enableDown = currentAlt >= cutoffDownAlt;

            for (int i = 0; i < numSteps; i++)
            {
                float dist = (i + 1) * stepSize;
                float maxD = 0f;

                Vector3 s0 = craftPos + beam0 * dist;
                Vector3 sU = craftPos + beamUp * dist;
                Vector3 sD = craftPos + beamDown * dist;

                // Apply the altitude gate to the horizontal and downward beams
                bool val0 = enableHoriz && (s0 - center).sqrMagnitude > bodyRadiusSq;
                bool valU = (sU - center).sqrMagnitude > bodyRadiusSq;
                bool valD = enableDown && (sD - center).sqrMagnitude > bodyRadiusSq;

                for (int vIdx = 0; vIdx < cachedVolumes.Count; vIdx++)
                {
                    var vol = cachedVolumes[vIdx];
                    if (vol == null) continue;

                    // If val0 is false, it skips sampling and maxD remains 0
                    if (val0)
                    {
                        float c = vol.SampleCoverage(s0, out float _, false);
                        if (!float.IsNaN(c) && c > maxD) maxD = c;
                    }
                    if (maxD < 1f && valU)
                    {
                        float c = vol.SampleCoverage(sU, out float _, false);
                        if (!float.IsNaN(c) && c > maxD) maxD = c;
                    }
                    if (maxD < 1f && valD)
                    {
                        float c = vol.SampleCoverage(sD, out float _, false);
                        if (!float.IsNaN(c) && c > maxD) maxD = c;
                    }
                }

                densitiesBuffer[i] = maxD;
            }
        }

        // --- Drawing Helpers ---
        private void DrawSweepLine(float angleDeg)
        {
            float angleRad = angleDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(angleRad);
            float sinA = Mathf.Sin(angleRad);
            int maxRadius = TEX_CENTER - 2;

            for (int r = 0; r < maxRadius; r++)
            {
                int px = TEX_CENTER + Mathf.RoundToInt(sinA * r);
                int py = TEX_CENTER + Mathf.RoundToInt(cosA * r);

                int stepIdx = Mathf.Clamp((int)((r / (float)maxRadius) * radarSteps), 0, radarSteps - 1);
                float dens = densitiesBuffer[stepIdx];

                Color32 targetColor;
                if (dens > radarThreshold)
                {
                    float displayDens = Mathf.Clamp01(dens * currentColorMultiplier);
                    byte cr = (byte)Mathf.Clamp(displayDens * 510f, 0f, 255f);
                    byte cg = (byte)Mathf.Clamp(510f - displayDens * 510f, 0f, 255f);
                    targetColor = new Color32(cr, cg, 0, 255);
                }
                else
                {
                    targetColor = new Color32(0, 67, 0, 255);
                }

                PaintBrush(px, py, targetColor);
            }
        }

        // Because 'px' & 'py' are bounded between 2 & 254 (128 +/- 126), array indices will mathematically never fall off the texture bounds
        // Branchless paint for huge efficiency gains. 
        private void PaintBrush(int px, int py, Color32 c)
        {
            int idx = py * TEX_SIZE + px;
            pixelBuffer[idx] = c;
            pixelBuffer[idx - 1] = c;
            pixelBuffer[idx + 1] = c;
            pixelBuffer[idx - TEX_SIZE] = c;
            pixelBuffer[idx + TEX_SIZE] = c;
        }

        private void GenerateOverlayIndices()
        {
            List<int> indices = new List<int>();
            int maxRadius = TEX_CENTER - 2;
            int[] ringRadii = { maxRadius / 3, (maxRadius * 2) / 3, maxRadius - 1 };

            foreach (int r in ringRadii)
            {
                int steps = Mathf.CeilToInt(2f * Mathf.PI * r);
                for (int i = 0; i < steps; i++)
                {
                    float a = (i / (float)steps) * Mathf.PI * 2f;
                    int px = TEX_CENTER + (int)(Mathf.Sin(a) * r);
                    int py = TEX_CENTER + (int)(Mathf.Cos(a) * r);
                    int idx = py * TEX_SIZE + px;
                    if (idx >= 0 && idx < TEX_SIZE * TEX_SIZE) indices.Add(idx);
                }
            }
            overlayIndices = indices.Distinct().ToArray();
        }

        private void ClearScreen()
        {
            Array.Copy(clearColors, pixelBuffer, clearColors.Length);
            radarTexture.SetPixels32(pixelBuffer);
            radarTexture.Apply();
            currentSweepAngle = 0f;
            lastDrawAngle = 0f;
        }

        // --- GUI Rendering ---
        public void OnGUI()
        {
            // Checks both standard toggle and the F2 hide button states
            if (showUI && !uiHidden)
            {
                GUI.skin = HighLogic.Skin;
                windowRect = GUILayout.Window(854124, windowRect, DrawWindow, "Weather Radar");

                // Constraint to keep the window trapped inside the boundaries of the user's resolution
                windowRect.x = Mathf.Clamp(windowRect.x, 0, Screen.width - windowRect.width);
                windowRect.y = Mathf.Clamp(windowRect.y, 0, Screen.height - windowRect.height);
            }
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.Box(radarTexture, GUILayout.Width(TEX_SIZE), GUILayout.Height(TEX_SIZE));
            GUILayout.Space(5);

            string shortLabel = (shortRange / 1000f).ToString("0") + "km";
            string longLabel = (longRange / 1000f).ToString("0") + "km";

            GUILayout.BeginHorizontal();

            // Lazy initialization of button style to prevent unnecessary garbage collection per frame
            if (richButtonStyle == null)
            {
                richButtonStyle = new GUIStyle(GUI.skin.button) { richText = true };
            }

            string gainText = "<color=orange>GAIN: MED</color>";
            if (gainState == 1) gainText = "<color=red>GAIN: HI</color>";
            else if (gainState == 2) gainText = "<color=green>GAIN: LO</color>";

            if (GUILayout.Button(gainText, richButtonStyle))
            {
                gainState = (gainState + 1) % 3;
                if (gainState == 0) currentColorMultiplier = baseColorMultiplier;
                else if (gainState == 1) currentColorMultiplier = baseColorMultiplier + 0.5f;
                else if (gainState == 2) currentColorMultiplier = Mathf.Max(0f, baseColorMultiplier - 0.5f);
            }

            if (GUILayout.Button(isGlobalMode ? "MODE: 360" : "MODE: FWD"))
            {
                isGlobalMode = !isGlobalMode;
                ClearScreen();
            }
            if (GUILayout.Button(isLongRange ? $"RNG: {longLabel}" : $"RNG: {shortLabel}"))
            {
                isLongRange = !isLongRange;
                ClearScreen();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}