"""Run real VibE-SVC2 checkpoints on local VOCALOID audio, outside the editor.

Imports pinned third-party source checkouts in place. No random-weight fallback,
native controller mapping, source/noise mixing, or automatic singer pitch shift.
Intermediate technique embeddings are an explicitly experimental extension.
"""
import argparse
import hashlib
import importlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
import types
from pathlib import Path


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load_file(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def git_revision(root):
    return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vibe-root", type=Path, required=True)
    parser.add_argument("--bigvgan-root", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--miku", type=Path, required=True)
    parser.add_argument("--tianyi", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--speedup", type=int, default=10)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Output directory must be new")
    if not 2 <= args.speedup <= 100:
        parser.error("speedup must be 2..100")
    args.vibe_root, args.bigvgan_root, args.assets = (p.resolve() for p in (args.vibe_root, args.bigvgan_root, args.assets))
    import numpy as np
    import parselmouth
    import soundfile as sf
    import torch
    import torch.nn.functional as functional
    import yaml
    from scipy.signal import resample_poly

    torch.set_num_threads(min(8, os.cpu_count() or 1))
    if not torch.cuda.is_available():
        raise RuntimeError("This experiment expects a verified CUDA runtime")
    device = torch.device("cuda")
    # Execute a real kernel before loading large checkpoints.
    _ = (torch.ones((32, 32), device=device) @ torch.ones((32, 32), device=device)).sum().item()
    print(f"CUDA ready: {torch.cuda.get_device_name()}, torch {torch.__version__}", flush=True)

    # BigVGAN uses top-level imports such as utils/env; isolate VibE's model
    # package under its own name to avoid accidentally resolving those imports.
    sys.path.insert(0, str(args.bigvgan_root))
    from bigvgan import BigVGAN, load_hparams_from_json
    from meldataset import get_mel_spectrogram
    package = types.ModuleType("ace_vibe_models")
    package.__path__ = [str(args.vibe_root / "models")]
    sys.modules[package.__name__] = package
    Unit2Mel = importlib.import_module("ace_vibe_models.model").Unit2Mel
    hubert_module = load_file("ace_vibe_hubert", args.vibe_root / "vencoder/hubert/hubert_model.py")
    volume_module = load_file("ace_vibe_volume", args.vibe_root / "utils/commons/energy_utils.py")

    config_path = args.assets / "vibe_timbre/config.yaml"
    checkpoint_path = args.assets / "vibe_timbre/model_200000.pt"
    hubert_path = args.assets / "hubert-soft-0d54a1f4.pt"
    vocoder_path = args.assets / "bigvgan"
    config = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    rate, hop = config["data"]["sampling_rate"], config["data"]["hop_size"]
    if (rate, hop, config["data"]["encoder"]) != (24000, 256, "hubertsoft"):
        raise ValueError("Unexpected checkpoint audio or semantic encoder configuration")
    args.output.mkdir(parents=True)
    print("Loading exact pretrained model state dictionaries", flush=True)
    # Load the already downloaded assets directly: the upstream HF mixin uses
    # parameters removed in newer huggingface_hub releases. Parameter matching
    # remains strict, including the documented weight-normalization layout.
    vocoder = BigVGAN(load_hparams_from_json(vocoder_path / "config.json"), use_cuda_kernel=False)
    vocoder_state = torch.load(vocoder_path / "bigvgan_generator.pt", map_location="cpu", weights_only=True, mmap=True)["generator"]
    has_weight_norm = any(k.endswith(".weight_g") for k in vocoder_state)
    if not has_weight_norm:
        vocoder.remove_weight_norm()
    vocoder.load_state_dict(vocoder_state, strict=True)
    if has_weight_norm:
        vocoder.remove_weight_norm()
    del vocoder_state
    vocoder.eval().to(device)
    diffusion, lut = config["model"]["diffusion"], config["model"]["LUT"]
    model = Unit2Mel(config["data"]["encoder_out_channels"], lut["n_spk"], lut["n_t_tech"],
                     vocoder.h.num_mels, diffusion["n_layers"], diffusion["n_chans"], diffusion["n_hidden"],
                     diffusion["timesteps"], diffusion["k_step_max"])
    checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=True, mmap=True)
    state = checkpoint["model"]
    state = {k.removeprefix("module."): v for k, v in state.items()}
    model.load_state_dict(state, strict=True)
    model.eval().to(device)
    del checkpoint, state
    hubert = hubert_module.HubertSoft()
    state = torch.load(hubert_path, map_location="cpu", weights_only=True, mmap=True)
    hubert.load_state_dict({k.removeprefix("module."): v for k, v in state.items()}, strict=True)
    hubert.eval().to(device)
    del state
    extractor = volume_module.Volume_Extractor(hop)
    straight = config["singing_techniques"]["timbre_tech"]["straight"]
    breathy = config["singing_techniques"]["timbre_tech"]["breathy"]

    report = {
        "status": "real_checkpoint_inference", "torch": torch.__version__,
        "cuda": torch.version.cuda, "gpu": torch.cuda.get_device_name(),
        "vibe_commit": git_revision(args.vibe_root), "bigvgan_commit": git_revision(args.bigvgan_root),
        "assets": {str(p.relative_to(args.assets)): digest(p) for p in [config_path, checkpoint_path, hubert_path,
                   vocoder_path / "config.json", vocoder_path / "bigvgan_generator.pt"]},
        "f0_extractor": "Parselmouth autocorrelation on clean source, no singer-mean scaling",
        "speaker_selection": "lowest fixed-noise denoising loss among trained female IDs; not a source identity estimate",
        "limitations": ["short sustained vowels, not a sentence or unseen-song identity benchmark",
                        "pretrained speaker table has no VOCALOID voices",
                        "intermediate style embeddings are experimental; only 0 and 1 are trained label endpoints",
                        "spectral metrics are not a perceptual speaker verification score"],
        "voices": {},
    }

    def pitch(x, count):
        sound = parselmouth.Sound(x.astype(np.float64), sampling_frequency=rate)
        p = sound.to_pitch_ac(time_step=hop / rate, pitch_floor=65, pitch_ceiling=1100, voicing_threshold=.6)
        raw = p.selected_array["frequency"]
        grid = np.arange(count) * hop / rate
        nearest = np.clip(np.rint((grid - p.x1) / p.dx).astype(int), 0, len(raw) - 1)
        uv = (raw[nearest] > 0) & (grid >= p.xs()[0]) & (grid <= p.xs()[-1])
        valid = raw > 0
        if valid.sum() < 3:
            raise ValueError("Insufficient reliable F0 in source")
        f0 = np.interp(grid, p.xs()[valid], raw[valid]).astype(np.float32)
        return f0, uv

    def write_variant(directory, name, x, source, voiced_samples):
        x = np.asarray(x, dtype=np.float32).reshape(-1)
        if not np.isfinite(x).all():
            raise ValueError("Non-finite model waveform")
        raw_count = len(x)
        sf.write(directory / f"{name}-raw.wav", x, rate, subtype="FLOAT")
        x = np.pad(x, (0, max(0, len(source) - len(x))))[:len(source)]
        # Common source voicing mask prevents generated silence/noise from
        # controlling the comparison's RMS match. No noise or dry signal is mixed.
        target_rms = np.sqrt(np.mean(source[voiced_samples].astype(np.float64) ** 2))
        rms = np.sqrt(np.mean(x[voiced_samples].astype(np.float64) ** 2))
        gain = target_rms / max(rms, 1e-9)
        gain = min(gain, .98 / max(float(np.max(np.abs(x))), 1e-9))
        preview = x * gain
        sf.write(directory / f"{name}.wav", preview, rate, subtype="PCM_16")
        return {"raw_samples": raw_count, "comparison_samples": len(source), "preview_gain_db": float(20 * np.log10(gain)),
                "raw_peak": float(np.max(np.abs(x))), "raw_voiced_rms": float(rms)}

    with torch.inference_mode():
        for voice, source_path in (("miku", args.miku), ("tianyi", args.tianyi)):
            print(f"Preparing {voice}", flush=True)
            directory = args.output / voice
            directory.mkdir()
            shutil.copyfile(source_path, directory / "native-original.wav")
            source, source_rate = sf.read(source_path, dtype="float32")
            if source.ndim != 1 or len(source) / source_rate > 15:
                raise ValueError("Use a mono source of <=15 seconds")
            from math import gcd
            divisor = gcd(source_rate, rate)
            audio = resample_poly(source, rate // divisor, source_rate // divisor).astype(np.float32)
            wav = torch.from_numpy(audio).to(device)[None, :]
            gt_mel = get_mel_spectrogram(wav, vocoder.h)
            frames = gt_mel.shape[-1]
            f0, uv = pitch(audio, frames)
            voiced_samples = np.repeat(uv, hop)
            voiced_samples = np.pad(voiced_samples, (0, max(0, len(audio) - len(voiced_samples))))[:len(audio)]
            if voiced_samples.sum() < rate // 4:
                raise ValueError("Too few voiced samples")
            audio16k = resample_poly(audio, 2, 3).astype(np.float32)
            units = hubert.units(torch.from_numpy(audio16k).to(device)[None, None, :])
            units = functional.interpolate(units.transpose(1, 2), size=frames, mode="nearest").transpose(1, 2)
            f0_tensor = torch.from_numpy(f0).to(device)[None, :, None]
            energy = extractor.extract(wav).to(device)[None, :, None]
            if energy.shape[1] != frames:
                raise ValueError("Energy/frame alignment mismatch")
            np.savez(directory / "source-features.npz", f0=f0, uv=uv, energy=energy.cpu().numpy(), source_rate=source_rate)
            # Choose an available trained embedding by an acoustic proxy rather
            # than suggesting an arbitrary ID is the original VOCALOID singer.
            candidates = [(n, i) for n, i in config["spk"].items() if "female" in n]
            cond_without_singer = model.unit_embed(units) + model.f0_embed((1 + f0_tensor / 700).log()) + model.energy_embed(energy)
            style = model.t_style_embed(torch.tensor([[straight]], device=device))
            target = model.decoder.norm_spec(gt_mel.transpose(1, 2)).transpose(1, 2)[:, None, :, :]
            scores = []
            for name, singer_id in candidates:
                cond = cond_without_singer + model.spk_embed(torch.tensor([[singer_id]], device=device)) + style
                losses = []
                for t in (150, 400, 650, 900):
                    torch.manual_seed(731)
                    noise = torch.randn_like(target)
                    losses.append(model.decoder.p_losses(target, torch.tensor([t], device=device), cond.transpose(1, 2), noise=noise).item())
                scores.append({"name": name, "id": singer_id, "loss": float(np.mean(losses))})
            scores.sort(key=lambda x: x["loss"])
            singer = scores[0]
            print(f"{voice}: selected reference embedding {singer['name']} ({singer['id']}), not a cloned voice", flush=True)
            source_info = {
                "source_sha256": digest(source_path), "source_samples": len(source), "source_rate": source_rate,
                "model_frames": frames, "f0_voiced_median_hz": float(np.median(f0[uv])),
                "singer_mean_pitch_normalization": False, "speaker_candidates": scores, "chosen_reference_speaker": singer,
                "variants": {},
            }
            source_info["variants"]["original"] = write_variant(directory, "original", audio, audio, voiced_samples)
            start = time.monotonic()
            vocoded = vocoder(gt_mel)[0, 0].cpu().numpy()
            source_info["variants"]["vocoder-only"] = write_variant(directory, "vocoder-only", vocoded, audio, voiced_samples)
            source_info["variants"]["vocoder-only"]["seconds"] = time.monotonic() - start
            # Also test the model's existing source-mel initialization branch.
            # This may preserve timbre better than full regeneration; each noise
            # starting point has its own straight control to isolate that effect.
            variants = [(0, "neutral", None), (.35, "breathy-035", None), (.7, "breathy-070", None), (1, "breathy-100", None),
                        (0, "source050-neutral", 50), (1, "source050-breathy", 50),
                        (0, "source100-neutral", 100), (1, "source100-breathy", 100),
                        (0, "source200-neutral", 200), (1, "source200-breathy", 200),
                        (0, "source400-neutral", 400), (1, "source400-breathy", 400)]
            for amount, label, source_step in variants:
                print(f"{voice}: real diffusion inference {label}", flush=True)
                # Constant here; a per-frame weight can use the same broadcast
                # shape [1,T,hidden] in a subsequent curve experiment.
                def style_override(layer, inputs, result):
                    value = layer.weight[straight] if amount == 0 else layer.weight[breathy] if amount == 1 else (
                        layer.weight[straight] + amount * (layer.weight[breathy] - layer.weight[straight]))
                    return value[None, None, :]
                hook = model.t_style_embed.register_forward_hook(style_override)
                start = time.monotonic()
                try:
                    torch.manual_seed(20261020)
                    mel = model(units, f0_tensor, energy, torch.tensor([[singer["id"]]], device=device),
                                timbre_style_id=torch.tensor([[straight]], device=device), infer=True,
                                gt_spec=gt_mel.transpose(1, 2) if source_step is not None else None,
                                k_step=source_step or 1000,
                                infer_speedup=args.speedup, method="dpm-solver++", use_tqdm=False)
                    generated = vocoder(mel.transpose(1, 2))[0, 0].cpu().numpy()
                finally:
                    hook.remove()
                details = write_variant(directory, label, generated, audio, voiced_samples)
                details["seconds"] = time.monotonic() - start
                details["style_embedding_fraction"] = amount
                details["source_mel_initialization"] = source_step is not None
                details["diffusion_start_step"] = source_step or 1000
                source_info["variants"][label] = details
                (args.output / "report.json").write_text(json.dumps({**report, "voices": {**report["voices"], voice: source_info}}, indent=2), encoding="utf-8")
            report["voices"][voice] = source_info
    report["cuda_peak_allocated_mib"] = torch.cuda.max_memory_allocated() / 1024 ** 2
    (args.output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"Completed real checkpoint comparison: {args.output}", flush=True)


if __name__ == "__main__":
    main()
