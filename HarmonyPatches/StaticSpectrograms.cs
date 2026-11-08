using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ImBlindedByTheLights.HarmonyPatches {
	// These only depend on the audio, so the games own static lights dont stop them either. Thats why this is optional
	[HarmonyPatch(typeof(BasicSpectrogramData), nameof(BasicSpectrogramData.ProcessSamples))]
	static class StaticSpectrograms {
		static bool frozenForAllCameras = false;

		static readonly List<float> silence = Enumerable.Repeat(0f, BasicSpectrogramData.kNumberOfSamples).ToList();

		static BasicSpectrogramData[] spectrogramData;
		static List<float>[] samplesBackup;
		static bool[] hasProcessedDataBackup;
		static Action[] applySpectrograms;

		static bool Prefix(List<float> processedSamples) {
			if(!frozenForAllCameras)
				return true;

			for(var i = 0; i < processedSamples.Count; i++)
				processedSamples[i] = 0f;

			return false;
		}

		public static void Clear() {
			frozenForAllCameras = false;
			spectrogramData = null;
			samplesBackup = null;
			hasProcessedDataBackup = null;
			applySpectrograms = null;
		}

		public static void FreezeForAllCameras() {
			frozenForAllCameras = Config.Instance.staticSpectrograms;
		}

		static Action ApplyIfActive(MonoBehaviour behaviour, Action update) {
			return () => {
				if(behaviour.isActiveAndEnabled)
					update();
			};
		}

		public static void InitPerCamera() {
			if(!Config.Instance.staticSpectrograms)
				return;

			spectrogramData = Object.FindObjectsOfType<BasicSpectrogramData>(true);
			samplesBackup = new List<float>[spectrogramData.Length];
			hasProcessedDataBackup = new bool[spectrogramData.Length];

			applySpectrograms = Object.FindObjectsOfType<Spectrogram>(true).Select(x => ApplyIfActive(x, x.Update))
				.Concat(Object.FindObjectsOfType<SpectrogramRow>(true).Select(x => ApplyIfActive(x, x.Update)))
				.Concat(Object.FindObjectsOfType<SpectrogramRowPropertyAnimator>(true).Select(x => ApplyIfActive(x, x.Update)))
				.Concat(Object.FindObjectsOfType<TransformSpectrogram>(true).Select(x => ApplyIfActive(x, x.Update)))
				.ToArray();
		}

		static void Apply() {
			for(var i = 0; i < applySpectrograms.Length; i++)
				applySpectrograms[i]();
		}

		public static void SetStatic() {
			if(spectrogramData == null)
				return;

			for(var i = 0; i < spectrogramData.Length; i++) {
				samplesBackup[i] = spectrogramData[i]._processedSamples;
				hasProcessedDataBackup[i] = spectrogramData[i]._hasProcessedData;

				spectrogramData[i]._processedSamples = silence;
				spectrogramData[i]._hasProcessedData = true;
			}

			Apply();
		}

		public static void Restore() {
			if(spectrogramData == null)
				return;

			// Marked as processed so that putting back the values does not advance their smoothing another time
			for(var i = 0; i < spectrogramData.Length; i++) {
				spectrogramData[i]._processedSamples = samplesBackup[i];
				spectrogramData[i]._hasProcessedData = true;
			}

			Apply();

			for(var i = 0; i < spectrogramData.Length; i++)
				spectrogramData[i]._hasProcessedData = hasProcessedDataBackup[i];
		}
	}
}
