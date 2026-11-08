using IPA.Utilities;
using System.Linq;
using UnityEngine;

namespace ImBlindedByTheLights.LightAdapters {
	// For things that only ever get driven by the lightshow and have no color we could usefully swap out.
	// Toggles the renderers instead of the GameObject as a lot of this has OnEnable / OnDisable logic
	class UnifiedDynamicRenderers : ILightAdapter {
		static readonly FieldAccessor<BufferedLightColorGroupEffectManager, MaterialPropertyBlockController[]>.Accessor FIELD_materialPropertyBlockControllers =
			FieldAccessor<BufferedLightColorGroupEffectManager, MaterialPropertyBlockController[]>.GetAccessor("_materialPropertyBlockControllers");

		public static UnifiedDynamicRenderers Create(BufferedLightColorGroupEffectManager t) {
			return new UnifiedDynamicRenderers(t.gameObject, FIELD_materialPropertyBlockControllers(ref t).SelectMany(x => x.renderers).ToArray());
		}

		Renderer[] renderers;
		bool[] backupEnabled;

		public GameObject gameObject { get; private set; }

		UnifiedDynamicRenderers(GameObject gameObject, Renderer[] renderers) {
			this.gameObject = gameObject;
			this.renderers = renderers.Where(x => x != null).Distinct().ToArray();
			backupEnabled = new bool[this.renderers.Length];
		}

		public void SetStatic() => SetActive(false);
		public void Restore() => SetActive(true);

		public void SetActive(bool active) {
			for(var i = 0; i < renderers.Length; i++) {
				if(active) {
					renderers[i].enabled = backupEnabled[i];
				} else {
					backupEnabled[i] = renderers[i].enabled;
					renderers[i].enabled = false;
				}
			}
		}

		public bool CheckShouldBeActiveWhenStatic() => false;
	}
}
