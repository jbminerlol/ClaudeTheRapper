"""Estimate tempo (BPM) and bar-0 offset of decoded stage music."""
import sys, wave, numpy as np
def load(paths):
    xs=[]
    for p in paths:
        w=wave.open(p); r=w.getframerate(); xs.append(np.frombuffer(w.readframes(w.getnframes()),np.int16).astype(float))
    n=min(map(len,xs)); return sum(x[:n] for x in xs), r
def onset(x, r, hop=128):
    frames=len(x)//hop; e=np.sqrt((x[:frames*hop].reshape(frames,hop)**2).mean(1))+1
    o=np.maximum(np.diff(np.log(e)),0); return o-o.mean(), r/hop
def bpm_estimate(o, fps, lo=70, hi=180):
    ac=np.correlate(o,o,'full')[len(o)-1:]
    # score each BPM by summed autocorrelation at 1,2,4,8 beats (bar consistency)
    best=None
    for bpm in np.arange(lo,hi,0.01):
        lag=60/bpm*fps; s=0
        for k in (1,2,4,8,16):
            L=lag*k; i=int(L); f=L-i
            if i+1<len(ac): s+=ac[i]*(1-f)+ac[i+1]*f
        if best is None or s>best[1]: best=(bpm,s)
    return best[0]
def phase(o,fps,bpm):
    beat=60/bpm*fps; t=np.arange(len(o))
    scores=[]
    for ph in np.arange(0,beat*4,0.5):  # bar phase: downbeats strongest
        idx=(ph+np.arange(0,len(o)/(beat*4))*beat*4).astype(int); idx=idx[idx<len(o)]
        scores.append((o[idx].mean(),ph/fps))
    return max(scores)[1]
if __name__=='__main__':
    x,r=load(sys.argv[1:]); o,fps=onset(x,r)
    b=bpm_estimate(o,fps); p=phase(o,fps,b)
    print('BPM %.2f  sec/bar %.4f  downbeat phase %.3fs  bars %.1f'%(b,240/b,p,len(x)/r/(240/b)))
