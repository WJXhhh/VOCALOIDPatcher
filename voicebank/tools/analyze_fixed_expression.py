"""Fixed timing, variable within-range articulation: raw-render verification.

No audio alignment, cropping, normalization or effects before measurement.
Audition copies share one crop and one fixed gain across all variants.
"""
import argparse
import json
import wave
import struct
from pathlib import Path
import numpy as np
from scipy.ndimage import uniform_filter1d
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.font_manager import FontProperties


def read(path):
    with wave.open(str(path), "rb") as wav:
        assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (1, 2, 44100)
        return np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").astype(float) / 32768


def main(directory, output, other):
    output.mkdir(parents=True, exist_ok=True)
    rows = json.loads((directory / "fixed-expression.json").read_text(encoding="utf-8"))
    samples = {r["name"]: read(directory / (r["name"] + ".wav")) for r in rows}
    envelopes = {k: np.sqrt(np.maximum(0, uniform_filter1d(v*v, 441, mode="constant"))) for k,v in samples.items()}
    time = np.arange(len(next(iter(samples.values())))) / 44100 - 1.5
    records = []
    for row in rows:
        name = row["name"]; prefix, suffix = name.split("-", 1)
        baseline = next(r for r in rows if r["name"] == prefix + "-baseline")
        raw, original = samples[name], samples[baseline["name"]]
        fixed = row["fixedRange"]
        bounds = lambda r: [(s["Begin"],s["Split"],s["End"],s["From"],s["To"],s["Stage"],s["Reverse"],s["MaterialMetadata"]) for s in r["rows"]]
        if fixed:
            assert bounds(row) == bounds(baseline) and row["positions"] == baseline["positions"]
            assert row["materials"] == baseline["materials"] and row["anchors"] == baseline["anchors"]
        arts = [s for s in baseline["rows"] if s["To"] is not None]
        target = arts[row["targetIndex"]]
        lo = max(0, (target["Begin"] - 3087) * 256)
        hi = (target["End"] - 3087) * 256
        window = np.arange(lo,hi)
        env, ref = envelopes[name], envelopes[baseline["name"]]
        peak = int(window[np.argmax(env[window])])
        scalar = float(np.dot(env[window],ref[window])/np.dot(ref[window],ref[window]))
        shape = float(np.linalg.norm(env[window]-scalar*ref[window])/max(1e-15,np.linalg.norm(env[window])))
        support = np.flatnonzero(raw)
        baseline_support = np.flatnonzero(original)
        # Source interpolation and synthesis windows have an existing overlap
        # footprint. Observe its actual extent; do not clip it to force a pass.
        support_equal = bool(np.array_equal(support[[0,-1]],baseline_support[[0,-1]]))
        contained = bool(support[0]>=baseline_support[0] and support[-1]<=baseline_support[-1])
        native_gain_equal = ([(f["Time"],f["Lane"],f["OriginalGain"]) for f in row["gainTrace"]] ==
            [(f["Time"],f["Lane"],f["OriginalGain"]) for f in baseline["gainTrace"]]) if row["gainTrace"] and baseline["gainTrace"] else None
        record = dict(name=name, fixed=fixed, boundariesEqual=bounds(row)==bounds(baseline), phonemesEqual=row["positions"]==baseline["positions"],
                      actualPcmSupportEqual=support_equal, pcmStartMilliseconds=round(float(time[support[0]]*1000),3),
                      pcmSupportContained=contained, pcmStartDeltaSamples=int(support[0]-baseline_support[0]), pcmEndDeltaSamples=int(support[-1]-baseline_support[-1]),
                      pcmEndMilliseconds=round(float(time[support[-1]]*1000),3), pcmExactBaseline=bool(np.array_equal(raw,original)),
                      peakMilliseconds=round(float(time[peak]*1000),3), peakRms=float(env[peak]),
                      rmsEnvelopeDifference=float(np.linalg.norm(env[window]-ref[window])/np.linalg.norm(ref[window])),
                      envelopeResidualAfterBestConstantGain=shape, originalNativeGainEqual=native_gain_equal)
        if fixed:
            assert contained, record
        if suffix in ("onset-slow","onset-fast","release-slow","release-fast"):
            assert support_equal, record
            assert not record["pcmExactBaseline"] and record["rmsEnvelopeDifference"] > .05 and shape > .025, record
            assert all(f["OriginalGain"] == f["AppliedGain"] for f in row["gainTrace"]), "Source-only test changed gain"
            current = [s for s in row["rows"] if s["To"] is not None][row["targetIndex"]]
            positions = {f["Frame"]: f for f in current["Frames"]}
            original_positions = {f["Frame"]: f for f in target["Frames"]}
            landmarks = {target["Begin"],target["Split"]-1,target["Split"],target["End"]-1} & original_positions.keys()
            assert all(positions[f]==original_positions[f] for f in landmarks)
            record["sourceLandmarksFixed"] = True
            decode = lambda f: struct.unpack("<f",struct.pack("<i",f["SourceBits"]))[0]
            for begin,end in ((target["Begin"],target["Split"]),(target["Split"],target["End"])):
                before = [decode(f) for f in target["Frames"] if begin<=f["Frame"]<end]
                after = [decode(f) for f in current["Frames"] if begin<=f["Frame"]<end]
                if before: assert min(after)>=min(before)-1e-6 and max(after)<=max(before)+1e-6
            record["sourceRangeContained"] = True
        if suffix == "restore": assert record["pcmExactBaseline"]
        if other:
            assert np.array_equal(raw, read(other / (name + ".wav"))), name
        records.append(record)
    result = dict(scope="one Chinese a, MIDI 60, VEL64, 500ms; neutral and pre-edited phoneme timing",
                  samples=len(rows), crossVersionExact=bool(other), records=records)
    (output/"metrics.json").write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    font = FontProperties(fname="C:/Windows/Fonts/msyh.ttc")
    fig,axes = plt.subplots(2,1,figsize=(10,7),sharex=True,constrained_layout=True)
    for ax,prefix,title in zip(axes,("original","edited"),("原有 timing 不动","预先调整过的 timing 也不动")):
        base = next(r for r in rows if r["name"]==prefix+"-baseline")
        onset = next(s for s in base["rows"] if s["From"]=="Sil" and s["To"]=="a")
        limits=[((f-3087)*256/44100-1.5)*1000 for f in (onset["Begin"],onset["End"])]
        ax.axvspan(*limits,color="#1976c1",alpha=.035)
        for key,label,color in (("baseline","原始表现","#6f7e8a"),("onset-slow","框内慢起音：只改源进度","#1976c1"),
                                ("onset-fast","框内快起音：只改源进度","#db7a23"),("reference-fit-source-gain","框内映射参考进度及力度","#8d53ad")):
            if prefix+"-"+key not in envelopes: continue
            ax.plot(time[::32]*1000,envelopes[prefix+"-"+key][::32],label=label,color=color,linewidth=1.4)
        for f,label in zip((onset["Begin"],onset["Split"],onset["End"]),("L","S","R")):
            x=((f-3087)*256/44100-1.5)*1000; ax.axvline(x,color="#444",ls=":",lw=.9); ax.text(x,.034 if label=="R" else .061,label,fontsize=9)
        ax.set_title(title,fontproperties=font); ax.grid(alpha=.2); ax.set_ylim(0,.065); ax.legend(prop=font,loc="upper right")
        ax.set_ylabel("10 ms RMS（同一音量比例）",fontproperties=font)
    axes[-1].set_xlabel("相对固定音符起点（毫秒）；L/S/R 为固定过渡边界",fontproperties=font)
    axes[-1].set_xlim(-100,270)
    fig.savefig(output/"fixed-boundaries.png",dpi=160); plt.close(fig)
    names=["original-"+s for s in ("baseline","onset-slow","onset-fast")]
    a,b=round(1.35*44100),round(2.1*44100)
    scale=min(4,.9/max(float(np.max(np.abs(samples[n][a:b]))) for n in names))
    montage=[]
    for name in names:
        clip=np.round(samples[name][a:b]*scale*32767).astype("<i2")
        with wave.open(str(output/(name+".wav")),"wb") as wav:
            wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(44100);wav.writeframes(clip.tobytes())
        montage.extend((clip,np.zeros(round(.35*44100),dtype="<i2")))
    with wave.open(str(output/"fixed-range-comparison.wav"),"wb") as wav:
        wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(44100);wav.writeframes(np.concatenate(montage).tobytes())
    print(json.dumps(records,indent=2))


if __name__ == "__main__":
    p=argparse.ArgumentParser();p.add_argument("--input",required=True,type=Path);p.add_argument("--output",required=True,type=Path)
    p.add_argument("--compare-version",type=Path)
    args=p.parse_args();main(args.input,args.output,args.compare_version)
