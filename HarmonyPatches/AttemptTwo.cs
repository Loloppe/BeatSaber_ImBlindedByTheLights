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

		// Same as above, for the videos brightness
		static SongTimeSyncedVideoPlayer[] videoPlayers;

		// Stuff that isnt driven by a light ID so we cant give it the correct color, this gets hidden / put into its static state
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
			videoPlayers = null;
			yeetedLights = null;
			fixedPositionThings = null;
			fixedLocalThings = null;
			lightsEnabled = true;

			FloatFxValues.Clear();
			StaticSpectrograms.Clear();
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

		// Lights only get removed from their manager in its LateUpdate. If one would get turned back on before that happened it would end up in there twice
		static void FlushLightManagers(LightWithIdManager[] managers) {
			for(var i = 0; i < managers.Length; i++)
				managers[i].LateUpdate();
		}

		static IEnumerable<Transform> GetCarParts() {
			var cars = Object.FindObjectsOfType<HydraulicCarJumpEffect>(true).Select(x => x._rigidbody)
				.Concat(Object.FindObjectsOfType<HydraulicCarSuspensionEffect>(true).Select(x => x._rigidbody))
				.Where(x => x != null && x.transform.parent != null)
				.Select(x => x.transform.parent).Distinct();

			return cars.SelectMany(x => x.GetComponentsInChildren<Rigidbody>(true)).Select(x => x.transform);
		}

		static IEnumerable<ILightAdapter> GetFireAdapters() {
			foreach(var x in Object.FindObjectsOfType<FireEffect>(true)) {
				yield return UnifiedDynamicRenderers.Create(x.gameObject, x._flipBookPropertyBlockController.renderers);
				yield return new UnifiedDynamicBehaviours(new Behaviour[] { x._bloomPrePassRenderer });

				// What makes the surroundings light up
				if(x._useEmissionColor) {
					if(x._emissionTextureColorPropertyBlockController != null)
						yield return new UnifiedMaterialColor(x._emissionTextureColorPropertyBlockController, FireEffectShaderHelper.emissionTexColorPropertyId, Color.clear);
				} else if(x._privatePointLightPropertyBlockController != null) {
					yield return new UnifiedMaterialColor(x._privatePointLightPropertyBlockController, FireEffectShaderHelper.privatePointLightColorPropertyId, Color.clear);
				}
			}
		}

		static IEnumerable<ILightAdapter> GetParticleAdapters() {
			foreach(var x in Object.FindObjectsOfType<ParticleSystemEmitEventEffect>(true))
				yield return new UnifiedSpawnedRenderers(x._particleSystemParentTransform);

			foreach(var x in Object.FindObjectsOfType<ParticleSystemContinuousEventEffect>(true)) {
				yield return UnifiedDynamicRenderers.Create(x.gameObject, x._particleSystems
					.Where(p => p != null && !p.isEmitting)
					.Select(p => (Renderer)p.GetComponent<ParticleSystemRenderer>()));
			}
		}

		// The characters only ever show up when told so by the map, same for the effects that come with them
		static IEnumerable<ILightAdapter> GetBTSCharacterAdapters() {
			foreach(var x in Object.FindObjectsOfType<BTSCharacterSpawnEventEffect>(true)) {
				var root = x._characterWrapper.parent != null ? x._characterWrapper.parent : x._characterWrapper;

				yield return UnifiedDynamicRenderers.Create(x.gameObject, root.GetComponentsInChildren<Renderer>(true)
					.Where(r => !r.gameObject.activeInHierarchy || r.GetComponentInParent<BTSCharacter>(true) != null));

				yield return new UnifiedDynamicBehaviours(root.GetComponentsInChildren<BloomPrePassLight>(true)
					.Where(l => !l.gameObject.activeInHierarchy));
			}
		}

		public static IEnumerator Init() {
			StaticSpectrograms.Clear();

			yield return null;
			var bcu = Object.FindObjectOfType<BeatmapCallbacksUpdater>();
			if(bcu == null)
				yield break;

			var controller = IPA.Utilities.ReflectionUtil.GetField<BeatmapCallbacksController, BeatmapCallbacksUpdater>(bcu, "_beatmapCallbacksController");

			if(controller == null)
				yield break;

			yield return null;

			// Both sides are static, thus the game takes care of it
			if(BasegameStaticLights.enabled) {
				StaticSpectrograms.FreezeForAllCameras();
				yield break;
			}

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

			// Moved by event boxes. These can be nested, so unlike the rings they need to be handled in local space.
			// Includes inactive things as they can be swapped in for the static side, see SwitchEffects
			var _fixedLocalThings = Object.FindObjectsOfType<LightRotationGroup>(true).SelectMany(x => x.xTransforms.Concat(x.yTransforms).Concat(x.zTransforms))
				.Concat(Object.FindObjectsOfType<LightTranslationGroup>(true).SelectMany(x => x.xTransforms.Concat(x.yTransforms).Concat(x.zTransforms)))
				// These arent neccesarily direct children of the effect, e.g. Pillar/RotationBase on BTS
				.Concat(Object.FindObjectsOfType<LightPairRotationEventEffect>(true).SelectMany(x => new[] { x._transformL, x._transformR }))
				.Concat(Object.FindObjectsOfType<LightPairSinMoveEventEffect>(true).SelectMany(x => new[] { x._transformL, x._transformR }))
				// Can be rotating directional lights, which is what lights up things like the clouds on BTS
				.Concat(Object.FindObjectsOfType<LightRotationEventEffect>(true).Select(x => x.transform))
				.Concat(Object.FindObjectsOfType<MovementBeatmapEventEffect>(true).SelectMany(x => x._transforms))
				.Concat(Object.FindObjectsOfType<SmoothStepPositionEventEffect>(true).Select(x => x.transform))
				.Concat(Object.FindObjectsOfType<SmoothStepPositionGroupEventEffect>(true).SelectMany(x => x._elements))
				.Concat(Object.FindObjectsOfType<MoveInDirectionEffect>(true).Select(x => x._transform))
				// Doesnt depend on the map at all, e.g. the clouds on Lizzo
				.Concat(Object.FindObjectsOfType<FloatingTransformEffect>(true).SelectMany(x => new[] { x.transform, x._rotationTransform }))
				.Concat(GetCarParts())
				.Where(x => x != null).Distinct().ToArray();

			var originalLocalRotations = _fixedLocalThings.Select(x => x.localRotation).ToArray();
			var originalLocalPositions = _fixedLocalThings.Select(x => x.localPosition).ToArray();

			var switchEffects = new SwitchEffects();
			var floatFx = FloatFxValues.FindAll();
			var originalFloatFxValues = floatFx.Select(FloatFxValues.Get).ToArray();

			switchEffects.ResetToDefault();
			FlushLightManagers(managers);

			foreach(var x in floatFx)
				FloatFxValues.Set(x, 0f);

			/*
			 * To find out what the current Env looks like when static lights is on,
			 * do the same thing the game would do and then remember the result
			 */
			foreach(var x in staticLightshowEvents)
				controller.TriggerBeatmapEvent(x);

			FlushLightManagers(managers);

			// The map has nothing that would change this again, so this can stay like this for both sides
			if(Config.Instance.staticWhenNoLights && !FilterBeatmapLightEvents.endedUpWithAnyLights) {
				StaticSpectrograms.FreezeForAllCameras();
				yield break;
			}

			var _staticColors = managers.Select(GetColors).ToArray();

			var switchAdapters = switchEffects.CreateAdapters().ToArray();

			var staticFloatFxValues = floatFx.Select(x => new KeyValuePair<(FloatFxGroupEffectTarget target, int groupId, int elementId), float>(x, FloatFxValues.Get(x)))
				// Moving things are controlled by the same setting as all of the other things that move
				.Where(x => !Config.Instance.keepRingSpinsInStatic || !(x.Key.target is MoveInDirectionEffect))
				.ToArray();

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

			switchEffects.RestoreOriginal();

			for(var i = 0; i < floatFx.Length; i++) {
				UnifiedFloatFx.Set(floatFx[i], originalFloatFxValues[i]);
				FloatFxValues.Set(floatFx[i], originalFloatFxValues[i]);
			}

			FlushLightManagers(managers);

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
				.Concat(switchAdapters)
				.Concat(GetFireAdapters())
				.Concat(GetParticleAdapters())
				.Concat(GetBTSCharacterAdapters())
				.Append(new UnifiedFloatFx(staticFloatFxValues))
				.ToArray();

			// Includes inactive ones as they can end up being swapped in for the static side
			bloomLights = Object.FindObjectsOfType<BloomPrePassLight>(true);
			videoPlayers = Object.FindObjectsOfType<SongTimeSyncedVideoPlayer>(true);
			colorBackup = managers.Select(x => new Color[LightWithIdManager.kMaxLightId + 1]).ToArray();
			staticColors = _staticColors;
			lightManagers = managers;

			StaticSpectrograms.InitPerCamera();

#if DEBUG
			Plugin.Log.Debug(string.Format("Found {0} light managers, {1} fixedPositionThings and {2} fixedLocalThings!", lightManagers.Length, fixedPositionThings?.Length, fixedLocalThings?.Length));
#endif
		}

		public static bool lightsEnabled { get; private set; } = true;

		public static bool isSwitchingLights => lightManagers != null;

		static void RefreshBloomLights() {
			for(var i = 0; i < bloomLights.Length; i++) {
				if(bloomLights[i].isActiveAndEnabled)
					bloomLights[i].Refresh();
			}

			for(var i = 0; i < videoPlayers.Length; i++) {
				if(videoPlayers[i].isActiveAndEnabled)
					videoPlayers[i].LateUpdate();
			}
		}

		public static void EnableLights() {
			if(lightsEnabled || lightManagers == null || !Config.Instance.enablePlugin)
				return;

			lightsEnabled = true;

			// Reverse so that things which ended up touching the same thing get back the correct state
			for(var i = yeetedLights.Length - 1; i >= 0; i--)
				yeetedLights[i].SetActive(lightsEnabled);

			FlushLightManagers(lightManagers);

			for(var i = 0; i < lightManagers.Length; i++) {
				SetColors(lightManagers[i], colorBackup[i]);
				ApplyColors(lightManagers[i]);
			}

			RefreshBloomLights();

			StaticSpectrograms.Restore();

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

			// Goes first as the spectrogram FX targets apply their value using these samples
			StaticSpectrograms.SetStatic();

			// A lot of the light thingy's have OnEnable/Disable methods - So SetActive instead of this is REALLY bad
			for(var i = 0; i < yeetedLights.Length; i++)
				yeetedLights[i].SetActive(lightsEnabled);

			FlushLightManagers(lightManagers);

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

	// The directional light globals (Used by e.g. the clouds on BTS) are only updated for the first camera each frame, but they differ per camera for us
	[HarmonyPatch(typeof(LightManager), "OnCameraPreRender")]
	static class RefreshDirectionalLightsForEachCamera {
		static void Prefix(ref int ___lastRefreshFrameNum) {
			if(LightSwitch.isSwitchingLights)
				___lastRefreshFrameNum = -1;
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
