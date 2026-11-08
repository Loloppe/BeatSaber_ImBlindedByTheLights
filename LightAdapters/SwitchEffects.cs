using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ImBlindedByTheLights.LightAdapters {
	// The effects which swap between different objects / textures. The map could have already used these by the time
	// we look at them, so they need to be put back into their default state to find out what they look like when static
	class SwitchEffects {
		readonly GameObjectIntSwitchEventEffect[] intSwitches;
		readonly int[] originalIntSwitchValues;

		readonly GameObjectSwitchEventEffect[] boostSwitches;

		readonly MeshRendererSwitchEventEffect[] rendererSwitches;
		readonly Renderer[] renderers;
		readonly bool[] originalRenderersEnabled;

		readonly TextureIntSwitchEventEffect[] textureSwitches;
		readonly Texture[] originalTextures;

		public SwitchEffects() {
			intSwitches = Object.FindObjectsOfType<GameObjectIntSwitchEventEffect>(true).Where(x => x._valueToListMap != null).ToArray();
			originalIntSwitchValues = intSwitches.Select(x => x._previousActiveIdx).ToArray();

			boostSwitches = Object.FindObjectsOfType<GameObjectSwitchEventEffect>(true);

			rendererSwitches = Object.FindObjectsOfType<MeshRendererSwitchEventEffect>(true);
			renderers = rendererSwitches.SelectMany(x => x._deactivateOnBoostRenderers.Concat(x._activateOnBoostRenderers)).Where(x => x != null).Distinct().ToArray();
			originalRenderersEnabled = renderers.Select(x => x.enabled).ToArray();

			textureSwitches = Object.FindObjectsOfType<TextureIntSwitchEventEffect>(true).Where(x => x._valueToTextureMap != null).ToArray();
			originalTextures = textureSwitches.Select(x => x._materialPropertyBlockController.materialPropertyBlock.GetTexture(x._texturePropertyId)).ToArray();
		}

		public void ResetToDefault() {
			foreach(var x in intSwitches)
				x.HandleBasicBeatmapEventData(new BasicBeatmapEventData(0f, x._beatmapEventType, x._defaultValue, 1f));

			foreach(var x in boostSwitches)
				x.ToggleObjects(false);

			foreach(var x in rendererSwitches)
				x.ToggleObjects(false);

			foreach(var x in textureSwitches)
				x.SetTextureByIndex(x._defaultIndex);
		}

		public IEnumerable<ILightAdapter> CreateAdapters() {
			var objects = intSwitches.SelectMany(x => x._gameObjectsValueLists.SelectMany(l => l.gameObjects))
				.Concat(boostSwitches.SelectMany(x => x._deactivateOnBoostObjects.Concat(x._activateOnBoostObjects)));

			yield return new UnifiedStaticGameObjects(objects);
			yield return new UnifiedStaticRenderers(renderers);

			foreach(var x in textureSwitches)
				yield return new UnifiedMaterialTexture(x._materialPropertyBlockController, x._texturePropertyId);
		}

		// Boost colors are always turned off again for the dynamic side, so there is nothing to do for those
		public void RestoreOriginal() {
			for(var i = 0; i < intSwitches.Length; i++) {
				var x = intSwitches[i];
				x.HandleBasicBeatmapEventData(new BasicBeatmapEventData(0f, x._beatmapEventType, originalIntSwitchValues[i], 1f));
			}

			for(var i = 0; i < renderers.Length; i++)
				renderers[i].enabled = originalRenderersEnabled[i];

			for(var i = 0; i < textureSwitches.Length; i++) {
				if(originalTextures[i] == null)
					continue;

				var x = textureSwitches[i];
				x._materialPropertyBlockController.materialPropertyBlock.SetTexture(x._texturePropertyId, originalTextures[i]);
				x._materialPropertyBlockController.ApplyChanges();
			}
		}
	}
}
