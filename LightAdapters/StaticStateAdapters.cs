using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ImBlindedByTheLights.HarmonyPatches;

namespace ImBlindedByTheLights.LightAdapters {
	abstract class StaticStateAdapter : ILightAdapter {
		public GameObject gameObject { get; protected set; }

		public abstract void SetStatic();
		public abstract void Restore();

		public void SetActive(bool active) {
			if(active) {
				Restore();
			} else {
				SetStatic();
			}
		}

		public bool CheckShouldBeActiveWhenStatic() => false;
	}

	// Things that are turned on / off by events. Only ever touches the ones that differ from their static state
	// as the lights in here need to re-register themselves, see LightSwitch.FlushLightManagers
	class UnifiedStaticGameObjects : StaticStateAdapter {
		readonly GameObject[] objects;
		readonly bool[] staticActive;
		readonly bool[] backupActive;

		// What is active at the time of creating this is what is considered to be the static state
		public UnifiedStaticGameObjects(IEnumerable<GameObject> objects) {
			this.objects = objects.Where(x => x != null).Distinct().ToArray();
			staticActive = this.objects.Select(x => x.activeSelf).ToArray();
			backupActive = new bool[this.objects.Length];
		}

		public override void SetStatic() {
			for(var i = 0; i < objects.Length; i++) {
				backupActive[i] = objects[i].activeSelf;

				if(backupActive[i] != staticActive[i])
					objects[i].SetActive(staticActive[i]);
			}
		}

		public override void Restore() {
			for(var i = 0; i < objects.Length; i++) {
				if(backupActive[i] != staticActive[i])
					objects[i].SetActive(backupActive[i]);
			}
		}
	}

	class UnifiedStaticRenderers : StaticStateAdapter {
		readonly Renderer[] renderers;
		readonly bool[] staticEnabled;
		readonly bool[] backupEnabled;

		// What is enabled at the time of creating this is what is considered to be the static state
		public UnifiedStaticRenderers(IEnumerable<Renderer> renderers) {
			this.renderers = renderers.Where(x => x != null).Distinct().ToArray();
			staticEnabled = this.renderers.Select(x => x.enabled).ToArray();
			backupEnabled = new bool[this.renderers.Length];
		}

		public override void SetStatic() {
			for(var i = 0; i < renderers.Length; i++) {
				backupEnabled[i] = renderers[i].enabled;
				renderers[i].enabled = staticEnabled[i];
			}
		}

		public override void Restore() {
			for(var i = 0; i < renderers.Length; i++)
				renderers[i].enabled = backupEnabled[i];
		}
	}

	class UnifiedDynamicBehaviours : StaticStateAdapter {
		readonly Behaviour[] behaviours;
		readonly bool[] backupEnabled;

		public UnifiedDynamicBehaviours(IEnumerable<Behaviour> behaviours) {
			this.behaviours = behaviours.Where(x => x != null).Distinct().ToArray();
			backupEnabled = new bool[this.behaviours.Length];
		}

		public override void SetStatic() {
			for(var i = 0; i < behaviours.Length; i++) {
				backupEnabled[i] = behaviours[i].enabled;

				if(backupEnabled[i])
					behaviours[i].enabled = false;
			}
		}

		public override void Restore() {
			for(var i = 0; i < behaviours.Length; i++) {
				if(backupEnabled[i])
					behaviours[i].enabled = true;
			}
		}
	}

	// The renderers below this get spawned while playing, so they cant be collected up front
	class UnifiedSpawnedRenderers : StaticStateAdapter {
		readonly Transform parent;
		readonly List<Renderer> renderers = new List<Renderer>();
		bool[] backupEnabled = new bool[0];

		public UnifiedSpawnedRenderers(Transform parent) {
			this.parent = parent;
		}

		public override void SetStatic() {
			parent.GetComponentsInChildren(true, renderers);

			if(backupEnabled.Length < renderers.Count)
				backupEnabled = new bool[renderers.Count];

			for(var i = 0; i < renderers.Count; i++) {
				backupEnabled[i] = renderers[i].enabled;
				renderers[i].enabled = false;
			}
		}

		public override void Restore() {
			for(var i = 0; i < renderers.Count; i++)
				renderers[i].enabled = backupEnabled[i];
		}
	}

	class UnifiedMaterialColor : StaticStateAdapter {
		readonly MaterialPropertyBlockController controller;
		readonly int propertyId;
		readonly Color staticColor;
		Color backupColor;

		public UnifiedMaterialColor(MaterialPropertyBlockController controller, int propertyId, Color staticColor) {
			this.controller = controller;
			this.propertyId = propertyId;
			this.staticColor = staticColor;
		}

		public override void SetStatic() {
			backupColor = controller.materialPropertyBlock.GetColor(propertyId);
			controller.materialPropertyBlock.SetColor(propertyId, staticColor);
			controller.ApplyChanges();
		}

		public override void Restore() {
			controller.materialPropertyBlock.SetColor(propertyId, backupColor);
			controller.ApplyChanges();
		}
	}

	class UnifiedMaterialTexture : StaticStateAdapter {
		readonly MaterialPropertyBlockController controller;
		readonly int propertyId;
		readonly Texture staticTexture;
		Texture backupTexture;

		// The texture thats set at the time of creating this is what is considered to be the static state
		public UnifiedMaterialTexture(MaterialPropertyBlockController controller, int propertyId) {
			this.controller = controller;
			this.propertyId = propertyId;
			staticTexture = controller.materialPropertyBlock.GetTexture(propertyId);
		}

		public override void SetStatic() {
			backupTexture = controller.materialPropertyBlock.GetTexture(propertyId);

			if(staticTexture == null || backupTexture == staticTexture)
				return;

			controller.materialPropertyBlock.SetTexture(propertyId, staticTexture);
			controller.ApplyChanges();
		}

		public override void Restore() {
			if(staticTexture == null || backupTexture == null || backupTexture == staticTexture)
				return;

			controller.materialPropertyBlock.SetTexture(propertyId, backupTexture);
			controller.ApplyChanges();
		}
	}

	// Everything that gets driven by these has its own way of applying the value, so just give it the value we want
	class UnifiedFloatFx : StaticStateAdapter {
		readonly KeyValuePair<(FloatFxGroupEffectTarget target, int groupId, int elementId), float>[] staticValues;

		public UnifiedFloatFx(IEnumerable<KeyValuePair<(FloatFxGroupEffectTarget target, int groupId, int elementId), float>> staticValues) {
			this.staticValues = staticValues.ToArray();
		}

		public override void SetStatic() {
			for(var i = 0; i < staticValues.Length; i++) {
				if(FloatFxValues.Get(staticValues[i].Key) != staticValues[i].Value)
					Set(staticValues[i].Key, staticValues[i].Value);
			}
		}

		public override void Restore() {
			for(var i = 0; i < staticValues.Length; i++) {
				var value = FloatFxValues.Get(staticValues[i].Key);

				if(value != staticValues[i].Value)
					Set(staticValues[i].Key, value);
			}
		}

		public static void Set((FloatFxGroupEffectTarget target, int groupId, int elementId) key, float value) {
			key.target.SetValue(key.groupId, key.elementId, value);
			ApplyImmediately(key.target);
		}

		// These only pick up the new value at some later point in the frame, which is too late for us
		static void ApplyImmediately(FloatFxGroupEffectTarget target) {
			switch(target) {
				case FloatFxGroupEffectCollectionTarget collection:
					foreach(var x in collection._floatFxGroupEffectTargets)
						ApplyImmediately(x);
					break;
				case CombineGroupIdToVector4FloatFxGroupEffectTarget combine:
					combine.HandleBeatmapCallbacksControllerDidProcessAllCallbacksThisFrame();
					break;
				case Parametric3SliceSpriteWidthEndFloatFxEffectTarget width:
					if(width._parametric3SliceSpriteController != null && width._parametric3SliceSpriteController.isActiveAndEnabled)
						width._parametric3SliceSpriteController.Refresh();
					break;
				case SpectrogramMultiplierFloatFxEffectTarget spectrogram:
					if(spectrogram._spectrogram != null && spectrogram._spectrogram.isActiveAndEnabled)
						spectrogram._spectrogram.Update();
					break;
			}
		}
	}
}
