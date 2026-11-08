using BeatmapSaveDataVersion3;
using HarmonyLib;
using IPA.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ImBlindedByTheLights.LightAdapters;
using Object = UnityEngine.Object;

namespace ImBlindedByTheLights.HarmonyPatches {
	// Whenever static lights are applied by the game itself the only way of having a custom color is to change the colors it uses
	[HarmonyPatch]
	static class ForceCustomStaticColor {
		[HarmonyTargetMethods]
		static IEnumerable<MethodBase> TargetMethods() {
			yield return AccessTools.Method(typeof(LightSwitchEventEffect), nameof(LightSwitchEventEffect.GetNormalColor));
			yield return AccessTools.Method(typeof(LightSwitchEventEffect), nameof(LightSwitchEventEffect.GetHighlightColor));
		}

		static void Postfix(ref Color __result) {
			if(!Config.Instance.enablePlugin || !Config.Instance.useCustomStaticColor)
				return;

			if(BasegameStaticLights.enabled || (!FilterBeatmapLightEvents.endedUpWithAnyLights && Config.Instance.staticWhenNoLights))
				__result = Config.Instance.staticColor;
		}
	}

	[HarmonyPatch(typeof(TrackLaneRingsRotationEffect), nameof(TrackLaneRingsRotationEffect.Start))]
	static class ForceRingRotationSnap {
		// This is kinda dirty, "removes" the rotation that happens at the start of a level
		static void Prefix(ref float ____startupRotationStep, ref int ____startupRotationPropagationSpeed, ref float ____startupRotationFlexySpeed) {
			if((!Config.Instance.staticInHeadset && !Config.Instance.staticOnDesktop) || Config.Instance.keepRingSpinsInStatic || !Config.Instance.enablePlugin)
				return;

			____startupRotationStep *= 1f;
			____startupRotationPropagationSpeed *= 50;
			____startupRotationFlexySpeed *= 4f;
		}
	}

	// The static lightshow we need to replicate depends on the environment that ends up being used
	[HarmonyPatch(typeof(GameplayCoreSceneSetupData), nameof(GameplayCoreSceneSetupData.TransformBeatmapData))]
	static class CaptureEnvironmentInfo {
		static void Prefix(GameplayCoreSceneSetupData __instance) => LightSwitch.environmentInfo = __instance.targetEnvironmentInfo;
	}

#if DEBUG
	public
#endif
	static class LightSwitch {
		static readonly FieldAccessor<LightWithIdManager, Action>.Accessor FIELD_didChangeSomeColorsThisFrameEvent =
			FieldAccessor<LightWithIdManager, Action>.GetAccessor(nameof(LightWithIdManager.didChangeSomeColorsThisFrameEvent));

		const int STATIC_LIGHTSHOW_LOAD_ATTEMPTS = 10;

		internal static EnvironmentInfoSO environmentInfo;

		// Everything which has a light ID gets its color from these, so swapping the color per ID covers all of the different light types
		static LightWithIdManager[] lightManagers;
		static Color[][] staticColors;
		static Color[][] colorBackup;

		// These only pick up a color change in their LateUpdate, which has already happened by the time we swap the colors
		static BloomPrePassLight[] bloomLights;

		// Stuff that isnt driven by a light ID so we cant give it the correct color, this just gets hidden
		public static ILightAdapter[] yeetedLights;

		// Stuff that needs to not move
		static Transform[] fixedPositionThings;
		static Quaternion[] staticRotations;
		static Vector3[] staticPositions;

		static Quaternion[] staticRotationBackup;
		static Vector3[] staticPositionBackup;

		// Same as above, but for stuff thats moved in local space
		static Transform[] fixedLocalThings;
		static Quaternion[] staticLocalRotations;
		static Vector3[] staticLocalPositions;

		static Quaternion[] staticLocalRotationBackup;
		static Vector3[] staticLocalPositionBackup;


		public static void Clear() {
			lightManagers = null;
			staticColors = null;
			colorBackup = null;
			bloomLights = null;
			yeetedLights = null;
			fixedPositionThings = null;
			fixedLocalThings = null;
			lightsEnabled = true;
		}

		// What the game loads instead of the maps lightshow when on static lights, see BeatmapDataLoaderVersion4.BeatmapDataLoader::GetBeatmapDataFromSaveData
		static BeatmapEventData[] GetStaticLightshowEvents(bool useEnvironmentLightshow = true) {
			var beatmapData = new BeatmapData(4);

			if(!useEnvironmentLightshow || environmentInfo?.defaultLightshowAsset == null) {
				DefaultEnvironmentEventsFactory.InsertDefaultEvents(beatmapData);
			} else {
				BeatmapDataLoaderVersion4.BeatmapDataLoader.LoadLightshow(
					beatmapData,
					JsonUtility.FromJson<BeatmapSaveDataVersion4.LightshowSaveData>(environmentInfo.defaultLightshowAsset.text),
					new BpmTimeProcessor(120f, new List<BpmChangeEventData>()),
					BeatmapDataLoaderUtils.GetEnvironmentKeywords(environmentInfo, BeatmapLevelDataVersion.Original),
					BeatmapDataLoaderUtils.GetEnvironmentLightGroups(environmentInfo),
					new BeatmapLightEventConverterNoConvert()
				);
			}

			beatmapData.ProcessRemainingData();
			beatmapData.ProcessAndSortBeatmapData();

			return beatmapData.allBeatmapDataItems.OfType<BeatmapEventData>().ToArray();
		}

		// Parsing the event boxes uses a pool that isnt thread safe, which other mods can be using in the background to load the map at the same time
		static BeatmapEventData[] TryGetStaticLightshowEvents(bool logFailure) {
			try {
				return GetStaticLightshowEvents();
			} catch(Exception ex) {
				if(logFailure)
					Plugin.Log.Warn(string.Format("Failed to load the static lightshow, using the default static events for this level (Which might be a lot darker): {0}", ex));

				return null;
			}
		}

		static Color[] GetColors(LightWithIdManager manager) {
			var colors = new Color[LightWithIdManager.kMaxLightId + 1];

			for(var i = 0; i < colors.Length; i++)
				colors[i] = manager.GetColorForId(i);

			return colors;
		}

		static void SetColors(LightWithIdManager manager, Color[] colors) {
			for(var i = 0; i < colors.Length; i++) {
				if(manager.GetColorForId(i) != colors[i])
					manager.SetColorForId(i, colors[i]);
			}
		}

		// Stuff that has multiple light IDs would otherwise only pick up the change in the managers LateUpdate
		static void ApplyColors(LightWithIdManager manager) {
			var localManager = manager;
			FIELD_didChangeSomeColorsThisFrameEvent(ref localManager)?.Invoke();
		}

		public static IEnumerator Init() {
			yield return null;
			var bcu = Object.FindObjectOfType<BeatmapCallbacksUpdater>();
			if(bcu == null)
				yield break;

			var controller = IPA.Utilities.ReflectionUtil.GetField<BeatmapCallbacksController, BeatmapCallbacksUpdater>(bcu, "_beatmapCallbacksController");

			if(controller == null)
				yield break;

			yield return null;

			// Both sides are static, thus the game takes care of it
			if(BasegameStaticLights.enabled)
				yield break;

			BeatmapEventData[] staticLightshowEvents = null;

			for(var i = 0; i < STATIC_LIGHTSHOW_LOAD_ATTEMPTS && staticLightshowEvents == null; i++) {
				if(i != 0)
					yield return null;

				staticLightshowEvents = TryGetStaticLightshowEvents(i == STATIC_LIGHTSHOW_LOAD_ATTEMPTS - 1);
			}

			// Not correct for this environment, but still better than having the static side show the normal lightshow
			if(staticLightshowEvents == null)
				staticLightshowEvents = GetStaticLightshowEvents(false);

			var managers = Object.FindObjectsOfType<LightWithIdManager>();
			var originalColors = managers.Select(GetColors).ToArray();

			// Moved by event boxes. These can be nested, so unlike the rings they need to be handled in local space
			var _fixedLocalThings = Object.FindObjectsOfType<LightRotationGroup>().SelectMany(x => x.xTransforms.Concat(x.yTransforms).Concat(x.zTransforms))
				.Concat(Object.FindObjectsOfType<LightTranslationGroup>().SelectMany(x => x.xTransforms.Concat(x.yTransforms).Concat(x.zTransforms)))
				.Where(x => x != null && x.gameObject.activeInHierarchy).Distinct().ToArray();

			var originalLocalRotations = _fixedLocalThings.Select(x => x.localRotation).ToArray();
			var originalLocalPositions = _fixedLocalThings.Select(x => x.localPosition).ToArray();

			/*
			 * To find out what the current Env looks like when static lights is on,
			 * do the same thing the game would do and then remember the result
			 */
			foreach(var x in staticLightshowEvents)
				controller.TriggerBeatmapEvent(x);

			// The map has nothing that would change this again, so this can stay like this for both sides
			if(Config.Instance.staticWhenNoLights && !FilterBeatmapLightEvents.endedUpWithAnyLights)
				yield break;

			var _staticColors = managers.Select(GetColors).ToArray();

			if(Config.Instance.useCustomStaticColor) {
				foreach(var colors in _staticColors) {
					for(var i = 0; i < colors.Length; i++) {
						if(colors[i].a <= 0)
							continue;

						var customColor = Config.Instance.staticColor;
						customColor.a = colors[i].a;
						colors[i] = customColor;
					}
				}
			}

			// Some of the static lightshows turn on boost colors, which would stick around for the non-static side otherwise
			controller.TriggerBeatmapEvent(new ColorBoostBeatmapEventData(0, false));

			for(var i = 0; i < managers.Length; i++) {
				SetColors(managers[i], originalColors[i]);
				ApplyColors(managers[i]);
			}

			var _fixedPositionThings = Object.FindObjectsOfType<TrackLaneRing>().Select(x => x.transform)
				.Concat(Object.FindObjectsOfType<LightPairRotationEventEffect>().SelectMany(x => x.transform.Cast<Transform>()))
				.Where(x => x.gameObject.activeInHierarchy).Except(_fixedLocalThings).ToArray();

			// We need to wait one more frame for these to be in the positions they should be
			yield return null;

			if(!Config.Instance.keepRingSpinsInStatic) {
				staticRotationBackup = new Quaternion[_fixedPositionThings.Length];
				staticPositionBackup = new Vector3[_fixedPositionThings.Length];
				staticRotations = _fixedPositionThings.Select(x => x.rotation).ToArray();
				staticPositions = _fixedPositionThings.Select(x => x.position).ToArray();

				staticLocalRotationBackup = new Quaternion[_fixedLocalThings.Length];
				staticLocalPositionBackup = new Vector3[_fixedLocalThings.Length];
				staticLocalRotations = _fixedLocalThings.Select(x => x.localRotation).ToArray();
				staticLocalPositions = _fixedLocalThings.Select(x => x.localPosition).ToArray();

				fixedPositionThings = _fixedPositionThings;
				fixedLocalThings = _fixedLocalThings;
			}

			for(var i = 0; i < _fixedLocalThings.Length; i++) {
				_fixedLocalThings[i].localRotation = originalLocalRotations[i];
				_fixedLocalThings[i].localPosition = originalLocalPositions[i];
			}

			yeetedLights = Object.FindObjectsOfType<ParticleSystemEventEffect>().Select(x => (ILightAdapter)new UnifiedDynamicGameObject(x.gameObject))
				.Concat(Object.FindObjectsOfType<BufferedLightColorGroupEffectManager>().Select(x => UnifiedDynamicRenderers.Create(x)))
				.ToArray();

			bloomLights = Object.FindObjectsOfType<BloomPrePassLight>();
			colorBackup = managers.Select(x => new Color[LightWithIdManager.kMaxLightId + 1]).ToArray();
			staticColors = _staticColors;
			lightManagers = managers;

#if DEBUG
			Plugin.Log.Debug(string.Format("Found {0} light managers, {1} fixedPositionThings and {2} fixedLocalThings!", lightManagers.Length, fixedPositionThings?.Length, fixedLocalThings?.Length));
#endif
		}

		public static bool lightsEnabled { get; private set; } = true;

		static void RefreshBloomLights() {
			for(var i = 0; i < bloomLights.Length; i++)
				bloomLights[i].Refresh();
		}

		public static void EnableLights() {
			if(lightsEnabled || lightManagers == null || !Config.Instance.enablePlugin)
				return;

			lightsEnabled = true;

			for(var i = 0; i < yeetedLights.Length; i++)
				yeetedLights[i].SetActive(lightsEnabled);

			for(var i = 0; i < lightManagers.Length; i++) {
				SetColors(lightManagers[i], colorBackup[i]);
				ApplyColors(lightManagers[i]);
			}

			RefreshBloomLights();

			if(fixedPositionThings == null)
				return;

			for(var i = 0; i < fixedPositionThings.Length; i++) {
				fixedPositionThings[i].rotation = staticRotationBackup[i];
				fixedPositionThings[i].position = staticPositionBackup[i];
			}

			for(var i = 0; i < fixedLocalThings.Length; i++) {
				fixedLocalThings[i].localRotation = staticLocalRotationBackup[i];
				fixedLocalThings[i].localPosition = staticLocalPositionBackup[i];
			}
		}

		public static void DisableLights() {
			if(!lightsEnabled || lightManagers == null || !Config.Instance.enablePlugin)
				return;

			lightsEnabled = false;

			// A lot of the light thingy's have OnEnable/Disable methods - So SetActive instead of this is REALLY bad
			for(var i = 0; i < yeetedLights.Length; i++)
				yeetedLights[i].SetActive(lightsEnabled);

			for(var i = 0; i < lightManagers.Length; i++) {
				for(var j = 0; j < colorBackup[i].Length; j++)
					colorBackup[i][j] = lightManagers[i].GetColorForId(j);

				SetColors(lightManagers[i], staticColors[i]);
				ApplyColors(lightManagers[i]);
			}

			RefreshBloomLights();

			if(fixedPositionThings == null)
				return;

			for(var i = 0; i < fixedPositionThings.Length; i++) {
				staticRotationBackup[i] = fixedPositionThings[i].rotation;
				staticPositionBackup[i] = fixedPositionThings[i].position;

				fixedPositionThings[i].rotation = staticRotations[i];
				fixedPositionThings[i].position = staticPositions[i];
			}

			for(var i = 0; i < fixedLocalThings.Length; i++) {
				staticLocalRotationBackup[i] = fixedLocalThings[i].localRotation;
				staticLocalPositionBackup[i] = fixedLocalThings[i].localPosition;

				fixedLocalThings[i].localRotation = staticLocalRotations[i];
				fixedLocalThings[i].localPosition = staticLocalPositions[i];
			}
		}
	}

	// (Ab)using this (Which is on all normal cameras) to add our own behaviour
	[HarmonyPatch(typeof(BloomPrePass), "Awake")]
	static class InterceptCameraRenders {
		static void Postfix(BloomPrePass __instance) {
			var cam = __instance.GetComponent<Camera>();

			if(cam == null || __instance.gameObject.GetComponent<LightSwitchHandler>() != null)
				return;

			__instance.gameObject.AddComponent<LightSwitchHandler>();
		}
	}


	class LightSwitchHandler : MonoBehaviour {
		Camera cam;

		void Awake() {
			cam = GetComponent<Camera>();
		}

		int culls = 0;

		void LateUpdate() => culls = 0;

		void OnPreCull() {
			if(culls++ != 0)
				return;

			//System.Console.WriteLine("OnPreCull {0} {1} {2}", cam.transform?.parent?.name, cam.stereoTargetEye, cam.stereoActiveEye);

			var isRenderingVR = cam.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono;

			if((isRenderingVR && Config.Instance.staticInHeadset) || (!isRenderingVR && Config.Instance.staticOnDesktop))
				LightSwitch.DisableLights();
		}

		void OnPostRender() {
			if(culls-- != 1)
				return;

			//System.Console.WriteLine("OnPostRender {0} {1} {2}", cam.transform?.parent?.name, cam.stereoTargetEye, cam.stereoActiveEye);

			LightSwitch.EnableLights();
		}
	}
}
