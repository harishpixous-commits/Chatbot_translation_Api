import re,time,json,sys
from pixous_engine import engine_manager as em
M=em.EngineManager()
CASES={
"paragraph_ctx":"The team shipped the release. It fixed the login bug.",
"list_continuation":"You can:\n- reset your password\n- update your email\n- contact support",
"negation":"Do not share your password with anyone.",
"bold":"Please read the **terms of service** before you continue.",
"code_inline":"Run `npm install` and then open `localhost:4200`.",
"emoji":"Great job! \U0001F389 See you soon \U0001F44B",
"zwj_emoji":"Family plan \U0001F468\u200d\U0001F469\u200d\U0001F467 available.",
"crlf":"First line\r\nSecond line\r\nThird line",
"fenced_code":"Example:\n```\nGET /api/ai/chat\n```\nThen check the reply.",
"nested_md":"- **Fast** setup\n- *Secure* by default\n  - with `HTTPS`",
"heading":"## Getting started\nOpen the app and sign in.",
"short_frags":"Done\nPress Test\nBack",
}
RNG={"ta":(0x0B80,0x0BFF),"hi":(0x0900,0x097F),"te":(0x0C00,0x0C7F),"ml":(0x0D00,0x0D7F),"kn":(0x0C80,0x0CFF)}
def wrong(b,L):
    lo,hi=RNG[L];return [c for c in b if (0x0900<=ord(c)<=0x0DFF and not lo<=ord(c)<=hi) or 0x1C50<=ord(c)<=0x1C7F or c=="\u2020"]
def checks(name,src,out,L):
    f=[]
    if wrong(out,L): f.append("wrongscript")
    sl=src.replace("\r\n","\n").count("\n"); ol=out.replace("\r\n","\n").count("\n")
    if sl!=ol: f.append("lines %d->%d"%(sl,ol))
    for m in re.findall("`[^`\n]+`",src):
        if m not in out: f.append("code_lost")
    if src.count("**")!=out.count("**"): f.append("bold_markers")
    for e in re.findall("[\U0001F000-\U0001FAFF]",src):
        if e not in out: f.append("emoji_lost");break
    if "```" in src and out.count("```")!=2: f.append("fence")
    if re.search(r"(?m)^\s*-\s",src) and len(re.findall(r"(?m)^\s*-\s",out))!=len(re.findall(r"(?m)^\s*-\s",src)): f.append("list_markers")
    return f
res={}
for mode in ("baseline","v14c"):
    orig=em._translate_structured
    if mode=="baseline": em._translate_structured=lambda model,items,s,t,*r:model.batch_translate(items,s,t)
    for L in RNG:
        t=time.time(); out=M.translate("en",L,list(CASES.values()))["translations"]; dt=time.time()-t
        res[(mode,L)]=(out,dt)
    em._translate_structured=orig
tot={"baseline":0,"v14c":0};n=0
for L in RNG:
    for (name,src),i in zip(CASES.items(),range(len(CASES))):
        row=[]
        for mode in ("baseline","v14c"):
            o=res[(mode,L)][0][i]; f=checks(name,src,o,L); tot[mode]+= (not f); row.append((f,o))
        n+=1
        if L in("ta","te") : print(L,name,"| BASE",row[0][0] or "ok","| V14C",row[1][0] or "ok"); print("   B:",repr(row[0][1])[:120]); print("   N:",repr(row[1][1])[:120])
print("objective-clean of",n,tot,"latency s (batch of 12)",{k:round(v[1],1) for k,v in res.items()})
