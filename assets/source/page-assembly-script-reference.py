"""Assemble generated book/hoof edits over the exact established reading sprite.
Only extraction, translation registration, compositing, scaling, and scheduling.
No drawing, anatomy morphing, or invented in-betweens.
"""
from pathlib import Path
import json, hashlib, shutil
import numpy as np
from PIL import Image,ImageDraw,ImageFilter,ImageFont

ROOT=Path(__file__).resolve().parent
APP=ROOT.parents[1]/'outputs'/'RainbowDashDesktop'
OUT=ROOT/'staged-working'; OUT.mkdir(exist_ok=True)
manifest=json.loads((APP/'assets'/'manifest.json').read_text('utf-8'))
base_raw=Image.open(ROOT.parent/'rebuild-natural'/'art'/'working-cel-5.png').convert('RGBA')
base=Image.open(APP/'assets'/'frames'/'working'/'005.png').convert('RGBA')
registration=manifest['working']['registration'][5]

def book_mask(im):
    a=np.asarray(im).astype(int)
    return (a[:,:,0]>50)&(a[:,:,0]>a[:,:,1]*1.8)&(a[:,:,0]>a[:,:,2]*1.8)&(a[:,:,3]>120)

reference=book_mask(base_raw)[275:416,223:326]
def aligned(cel):
    candidate=book_mask(cel)
    scores=[]
    for dy in range(-9,10):
        for dx in range(-9,10):
            region=candidate[275-dy:416-dy,223-dx:326-dx]
            scores.append((int(np.count_nonzero(region!=reference)),abs(dx)+abs(dy),dx,dy))
    _,_,dx,dy=min(scores)
    shifted=Image.new('RGBA',(512,512)); shifted.alpha_composite(cel,(dx,dy))
    return shifted,[dx,dy]

def to_runtime(cel,mask,background=None):
    slot=Image.new('RGBA',(640,640)); slot.alpha_composite(cel,(64,64))
    maskslot=Image.new('L',(640,640)); maskslot.paste(mask,(64,64))
    scale=registration['scale']; size=(round(640*scale),round(640*scale))
    slot=slot.resize(size,Image.Resampling.LANCZOS)
    maskslot=maskslot.resize(size,Image.Resampling.LANCZOS)
    result=Image.new('RGBA',(512,512)); result.alpha_composite(slot,tuple(registration['offset']))
    maskout=Image.new('L',(512,512)); maskout.paste(maskslot,tuple(registration['offset']))
    return Image.composite(result,base if background is None else background,maskout),maskout

edits={}; raw_edits={}; records=[]
for name,file in [('page','page-turn-sheet.png'),('hoof','hoof-transition-sheet.png')]:
    sheet=Image.open(ROOT/file).convert('RGBA'); assert sheet.size==(1536,1024)
    frames=[]; raw_edits[name]=[]
    for i in range(6):
        x,y=i%3*512,i//3*512
        raw=sheet.crop((x,y,x+512,y+512)); raw,shift=aligned(raw)
        # Freeze eyes/head/body/cloud/tail. Only replace book+moving forehoof.
        mask=Image.new('L',(512,512)); d=ImageDraw.Draw(mask)
        d.polygon([(218,282),(249,263),(273,248),(295,205),(403,205),(403,418),(218,418)],fill=255)
        # Include the paper silhouette wherever it passes in front of the muzzle.
        a=np.asarray(raw).astype(int)
        cream=(a[:,:,0]>205)&(a[:,:,1]>180)&(a[:,:,2]<220)&(a[:,:,3]>100)
        roi=np.zeros((512,512),dtype=bool); roi[200:318,213:401]=True
        paper=Image.fromarray(((cream&roi)*255).astype('uint8')).filter(ImageFilter.MaxFilter(5))
        mask=Image.fromarray(np.maximum(np.asarray(mask),np.asarray(paper)))
        frame,runtime_mask=to_runtime(raw,mask)
        frames.append(frame)
        raw_edits[name].append((raw,mask))
        records.append({'source':name,'source_cel':i,'registration_translation':shift,'runtime_transform':registration,'composite_region':'Book/right hoof only; eyes, body/cloud/tail locked to existing frame005','outside_mask_unchanged':bool(np.array_equal(np.asarray(frame)[np.asarray(runtime_mask)==0],np.asarray(base)[np.asarray(runtime_mask)==0]))})
    edits[name]=frames

# Rest the free hoof on the top of the book for ALL quiet/blink cels, so the
# page gesture starts and ends at that same contact instead of teleporting
# between the lower cover and page corner. Keep each cel's authored eyelids.
rest_frames=[]
for i in range(6):
    prior=Image.open(APP/'assets'/'frames'/'working'/f'{i:03}.png').convert('RGBA')
    updated,_=to_runtime(*raw_edits['hoof'][0],background=prior)
    rest_frames.append(updated)
    updated.save(OUT/f'{i:03}.png',optimize=True)
base=rest_frames[5]
for name in edits:
    edits[name]=[to_runtime(raw,mask)[0] for raw,mask in raw_edits[name]]
edits['hoof'][0]=base.copy()

# Source hoof height orders: a small rise first, then curling onto the page corner.
lift=[0,2,1,4,3]
sequence=[('hoof',k) for k in lift]+[('page',k) for k in range(5)]+[('hoof',k) for k in reversed(lift)]+[('rest',5)]
delays=[55,55,55,55,70,70,90,130,130,100,55,55,55,55,55,100]
assert len(sequence)==len(delays)==16
action=[]
for i,(name,k) in enumerate(sequence):
    frame=base.copy() if name=='rest' else edits[name][k]
    frame.save(OUT/f'{i+6:03}.png',optimize=True); action.append(frame)
profile=manifest['working']['natural_playback']
profile.update(action_frames=list(range(6,22)),action_delays_ms=delays,action_initial_min_ms=3500,action_initial_max_ms=5500,action_min_ms=9000,action_max_ms=14000)
unique=len({hashlib.sha256(f.tobytes()).hexdigest() for f in rest_frames+action})
manifest['working'].update(frames=22,native_frames=unique,authored_key_poses=unique,unique_frames=unique,
    action_source_files=['source/working-page-turn.png','source/working-hoof-transition.png'],
    selected_additional_source_cels=10,
    action_sequence=[{'runtime_frame':i+6,'source':n,'source_cel':k,'delay_ms':delays[i]} for i,(n,k) in enumerate(sequence)],
    timing_note='Six reading/blink cels retain their eyelids and rest the free hoof on the book top. Ten generated source cels are selected; some coincide with the revised rest. Return hoof positions reverse existing poses and endpoint reuses reading005; repeats are not counted as new authored poses.')
(OUT/'durations.json').write_text(json.dumps([180]*6+delays),'utf-8')
(ROOT/'manifest-with-page.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),'utf-8')
(ROOT/'page-assembly-record.json').write_text(json.dumps({'additional_selected_key_poses':10,'runtime_action_frames':16,'action_duration_ms':sum(delays),'source_maps':records,'sequence':manifest['working']['action_sequence']},ensure_ascii=False,indent=2),'utf-8')

# Preview at actual desktop scale plus an enlarged view for inspecting the page.
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',20)
def preview_frame(i):
    im=action[i] if i is not None else base
    board=Image.new('RGB',(560,350),'#182335')
    for left,size in [(8,200),(232,300)]:
        tile=im.resize((size,size),Image.Resampling.LANCZOS); board.paste(tile,(left,22+(300-size)),tile)
    ImageDraw.Draw(board).text((16,12),'实际桌宠大小',font=font,fill='#a9bed5')
    ImageDraw.Draw(board).text((310,12),'翻页细节',font=font,fill='#a9bed5')
    return board
gif=[preview_frame(None)]+[preview_frame(i) for i in range(16)]+[preview_frame(None)]
gif[0].save(ROOT/'读书翻页-动态预览.gif',save_all=True,append_images=gif[1:],duration=[1200]+delays+[1500],loop=0,disposal=2)
contact=Image.new('RGB',(1200,600),'#182335')
for i,frame in enumerate(action):
    tile=frame.resize((200,200),Image.Resampling.LANCZOS); contact.paste(tile,((i%6)*200,(i//6)*200),tile)
contact.save(ROOT/'page-action-contact.jpg')
action[7].save(ROOT/'翻页关键姿态.png')
print(json.dumps({'duration_ms':sum(delays),'frames':len(action),'frozen_regions':all(v['outside_mask_unchanged'] for v in records),'sequence':sequence},ensure_ascii=False))
