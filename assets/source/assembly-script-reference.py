"""Deterministic cel extraction, landmark registration, timing and previews.
Does not draw/retouch character artwork or interpolate/morph silhouettes.
"""
from pathlib import Path
from collections import deque
import json, hashlib, shutil
import numpy as np
from PIL import Image, ImageOps, ImageDraw, ImageFont

ROOT=Path(__file__).resolve().parent
ART=ROOT/'art'
OUT=ROOT/'staged-assets'
OUT.mkdir(exist_ok=True)
STATES=['received','working','interrupted','completed','idle']
LABELS=['收到任务','处理任务','任务中断','任务完成','安静待机']
FILES={'received':'received-sheet.png','working':'working-sheet-v1.png','interrupted':'interrupted-sheet.png','completed':'completed-sheet.png','idle':'idle-sheet-v1.png'}
# Common skull-width calibration from source ear/face landmarks, NOT silhouette fitting.
SCALES={'received':1.0,'working':.76,'interrupted':.80,'completed':.80,'idle':.84}
contract=json.loads((ROOT/'natural-playback-contract.json').read_text('utf-8-sig'))

def components(alpha):
    mask=alpha>64
    seen=np.zeros(mask.shape,dtype=bool)
    hh,ww=mask.shape
    found=[]
    # Flood fill starts on a coarse lattice, then verifies every resulting large component.
    for y in range(0,hh,12):
        for x in range(0,ww,12):
            if not mask[y,x] or seen[y,x]: continue
            q=deque([(x,y)]); seen[y,x]=True
            left=right=x; top=bottom=y; count=0
            while q:
                a,b=q.popleft(); count+=1
                left=min(left,a); right=max(right,a); top=min(top,b); bottom=max(bottom,b)
                for nx,ny in ((a-1,b),(a+1,b),(a,b-1),(a,b+1)):
                    if 0<=nx<ww and 0<=ny<hh and mask[ny,nx] and not seen[ny,nx]:
                        seen[ny,nx]=True; q.append((nx,ny))
            if count>18000: found.append({'bbox':[left,top,right+1,bottom+1],'pixels':count})
    assert len(found)==6, found
    found.sort(key=lambda c:(int(((c['bbox'][1]+c['bbox'][3])/2)//512),(c['bbox'][0]+c['bbox'][2])/2))
    return found

def support_anchor(image):
    a=np.asarray(image)
    blue=(a[:,:,3]>100)&(a[:,:,2].astype(int)>a[:,:,0]+25)&(a[:,:,1].astype(int)>a[:,:,0]+20)
    yy,xx=np.where(blue)
    y=int(np.max(yy))
    xs=np.where(blue[max(0,y-6):y+1].any(axis=0))[0]
    runs=np.split(xs,np.where(np.diff(xs)>1)[0]+1)
    runs=[v for v in runs if len(v)>3]
    rear=runs[0]
    return float((rear[0]+rear[-1])/2),y

manifest={}; allframes={}; stats=[]
source=OUT/'source'; source.mkdir(exist_ok=True)
for state,label in zip(STATES,LABELS):
    raw=Image.open(ART/FILES[state]).convert('RGBA')
    assert raw.size==(1536,1024)
    comps=components(np.asarray(raw)[:,:,3])
    frames=[]; transforms=[]
    for idx,c in enumerate(comps):
        col,row=idx%3,idx//3
        l,t,r,b=c['bbox']; pad=3
        box=(max(0,l-pad),max(0,t-pad),min(raw.width,r+pad),min(raw.height,b+pad))
        crop=raw.crop(box)
        # Keep source coordinates in a padded local slot. Recovers art crossing cell lines.
        slot=Image.new('RGBA',(640,640))
        slot.alpha_composite(crop,(box[0]-col*512+64,box[1]-row*512+64))
        if state=='received': slot=ImageOps.mirror(slot)
        if state in ('received','interrupted','completed'):
            ax,ay=support_anchor(slot); tx,ty=220,480
        else:
            bb=Image.fromarray((np.asarray(slot)[:,:,3]>64).astype('uint8')*255).getbbox()
            ax,ay=(bb[0]+bb[2])/2,bb[3]-1; tx,ty=256,480
        scale=SCALES[state]
        resized=slot.resize((round(640*scale),round(640*scale)),Image.Resampling.LANCZOS)
        offset=(round(tx-ax*scale),round(ty-ay*scale))
        frame=Image.new('RGBA',(512,512)); frame.alpha_composite(resized,offset)
        # Exact fully transparent RGB normalization only; preserve all generated alpha edges.
        a=np.array(frame); a[a[:,:,3]==0,:3]=0; frame=Image.fromarray(a)
        bb=frame.getbbox()
        assert bb and min(bb[0],bb[1],512-bb[2],512-bb[3])>=2,(state,idx,bb)
        frames.append(frame)
        transforms.append({'source_component_bbox':c['bbox'],'source_anchor': [ax,ay],'target_anchor':[tx,ty],'scale':scale,'offset':offset,'mirrored':state=='received','final_bbox':bb})
    source_order=[0,1,2,3,5,4] if state=='completed' else list(range(6))
    if state=='completed':
        frames=[frames[k] for k in source_order]
        transforms=[dict(transforms[k],source_cel_index=k) for k in source_order]
    else:
        transforms=[dict(v,source_cel_index=k) for k,v in enumerate(transforms)]
    folder=OUT/'frames'/state; folder.mkdir(parents=True,exist_ok=True)
    profile=contract[state]['natural_playback']
    delays=profile.get('frame_delays_ms',[180]*6)
    for idx,frame in enumerate(frames): frame.save(folder/f'{idx:03}.png',optimize=True)
    (folder/'durations.json').write_text(json.dumps(delays),'utf-8')
    shutil.copy2(ART/FILES[state],source/(state+'.png'))
    prompts=list(ART.glob(state+'.prompt.*'))
    for p in prompts: shutil.copy2(p,source/p.name)
    manifest[state]={'label':label,'width':512,'height':512,'pixel_art':False,'rendering':'smooth','frames':6,'native_frames':6,'authored_key_poses':6,'frame_directory':'frames/'+state,'gif':state+'.gif','source_type':'Newly generated G4-style key cels based on original-show expression research; not original show footage','source_file':'source/'+state+'.png','source_dimensions':[1536,1024],'source_grid':[3,2],'native_cel_dimensions':[512,512],'source_sha256':hashlib.sha256((ART/FILES[state]).read_bytes()).hexdigest(),'natural_playback':profile,'registration':transforms,'timing_note':'Six authored key poses, variable holds; no invented 24-frame claim or optical-flow face morphing. WPF renders on composition refresh.'}
    allframes[state]=frames
    stats.append({'state':state,'frames':6,'size':[512,512],'registration':transforms,'closed_eye_cels': {'received':[3],'working':[2],'interrupted':[4],'completed':[2,3,4,5],'idle':[0,1,2,3,4,5]}[state]})

# Auxiliary labels are explicit variants, not additional bespoke animation claims.
for target,original,label in [('waiting','working','等待回应'),('error','interrupted','遇到问题')]:
    shutil.copytree(OUT/'frames'/original,OUT/'frames'/target,dirs_exist_ok=True)
    manifest[target]=dict(manifest[original],label=label,frame_directory='frames/'+target,gif=target+'.gif',variant_of=original)
    manifest[target]['natural_playback']={'mode':'hold','grounded':True,'hold_frame':5,'rest_frame':5}

(OUT/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),'utf-8')
(ROOT/'asset-qa.json').write_text(json.dumps(stats,ensure_ascii=False,indent=2),'utf-8')
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',26)
small=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',17)
def sample(state,t):
    p=contract[state]['natural_playback']
    if state=='working':
        if t<3300:return 0
        if t<3335:return 1
        if t<3380:return 2
        if t<3445:return 3
        return 5
    delays=p['frame_delays_ms']
    if state=='idle': t%=sum(delays)
    for idx,d in enumerate(delays):
        if t<d:return idx
        t-=d
    return 5

def board(t=None):
    canvas=Image.new('RGB',(1600,400),'#121b2a')
    draw=ImageDraw.Draw(canvas)
    peaks=[1,0,3,3,0]
    subtitles=['抬蹄回应 · 闭眼点头','低头阅读 · 偶尔眨眼','短暂一怔 · 歪头疑惑','弯月笑眼 · 展翅庆祝','云上闭眼 · 安静休息']
    for k,(s,label,sub) in enumerate(zip(STATES,LABELS,subtitles)):
        tile=allframes[s][peaks[k] if t is None else sample(s,t)].resize((300,300),Image.Resampling.LANCZOS)
        canvas.paste(tile,(k*320+10,8),tile)
        draw.text((k*320+160,321),label,font=font,fill='#e9f6ff',anchor='mm')
        draw.text((k*320+160,361),sub,font=small,fill='#a9bed5',anchor='mm')
    return canvas
board().save(ROOT/'五种状态-重构预览.png')
# Browser GIF viewers clamp tiny delays; use a uniform 50ms review sampling grid.
# Runtime PNG/manifest timing retains the exact35/45/65ms blink schedule.
times=list(range(0,5001,50))
preview=[board(t) for t in times[:-1]]
delays=[times[i+1]-times[i] for i in range(len(times)-1)]
preview[0].save(ROOT/'五种状态-动态预览.gif',save_all=True,append_images=preview[1:],duration=delays,loop=0,disposal=2,optimize=False)
for state in STATES:
    # GIF is a review fallback. Actual working timing is randomized by the app.
    tt=list(range(0,5001,50))
    ff=[allframes[state][sample(state,t)] for t in tt[:-1]]
    dd=[tt[i+1]-tt[i] for i in range(len(tt)-1)]
    ff[0].save(OUT/(state+'.gif'),save_all=True,append_images=ff[1:],duration=dd,loop=0,disposal=2)
for target,original in [('waiting','working'),('error','interrupted')]: shutil.copy2(OUT/(original+'.gif'),OUT/(target+'.gif'))
print(json.dumps({'states':STATES,'source_key_poses':30,'output':str(OUT),'board':str(ROOT/'五种状态-重构预览.png')},ensure_ascii=False))
