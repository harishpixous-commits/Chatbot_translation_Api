"""Unit tests for pixous_engine.script_check (objective script anomaly only)."""
import sys
from pixous_engine.script_check import check_items
ok=0;bad=0
def chk(n,c):
    global ok,bad
    print(("PASS " if c else "FAIL ")+n); ok+=c; bad+=(not c)
def flag(t,src,out): return check_items(t,[src],[out])["script_anomaly_items"]==[0]
def clean(t,src,out): r=check_items(t,[src],[out]); return r["checked"] and r["script_anomaly_items"]==[]
# positives: strings observed from the real chain (garble)
chk("arabic tail in hi flagged", flag("hi","Please enter your email.","अपना ईमेल पता दर्ज करें ڊائن।"))
chk("meitei in ml flagged", flag("ml","Your order shipped.","നിങ്ങളുടെ ഓർഡർ അയച്ചു ꯃꯃꯤꯡꯁꯤꯡ."))
chk("han+bengali tail in kn flagged", flag("kn","Enter","ನಮೂದ寶 থাকিল"))
chk("arabic suffix in ta flagged", flag("ta","Sign in","உள்நுழையالم"))
chk("meitei prefix in hi flagged", flag("hi","Home","ꯅꯍꯥꯛꯀꯤ घर"))
# negative controls: must NOT flag
chk("clean ta", clean("ta","Home","வீடு"))
chk("latin brand/DOM allowed", clean("ta","Shadow DOM greeting","நிழல் DOM வாழ்த்து"))
chk("digits and punctuation", clean("hi","Order 12345, total $9.99!","ऑर्डर 12345, कुल $9.99!"))
chk("URL and email kept", clean("te","Visit https://a.example/x or a@b.co","https://a.example/x లేదా a@b.co సందర్శించండి"))
chk("code kept", clean("kn","Run `npm i`","`npm i` ಅನ್ನು ಚಲಾಯಿಸಿ"))
chk("quoted Arabic in source preserved", clean("hi",'He said "مرحبا".','उसने कहा "مرحبا"।'))
chk("quoted Cyrillic name in source preserved", clean("ml","Meet Иван today","ഇന്ന് Иван നെ കാണുക"))
chk("ZWJ/ZWNJ in ml not letters", clean("ml","a","ക്\u200dഷ"))
chk("en target: only Latin ok", clean("en","வீடு","Home"))
chk("en target flags Tamil leak when source has none", flag("en","Hello","Hello வீடு"))
chk("unknown target not checked", check_items("xx",["a"],["ꯅ"])["checked"] is False)
chk("per-item indices", check_items("hi",["a","b","c"],["घर","घرر","घर"])["script_anomaly_items"]==[1])
# realistic false-positive controls
chk("Hinglish: Latin words inside hi output", clean("hi","Open the Settings page","Settings page खोलें"))
chk("Latin personal names in ta", clean("ta","Ask Priya or John","Priya அல்லது John ஐ கேளுங்கள்"))
chk("modifier apostrophe U+02BC in ta", clean("ta","Don't","Don\u02bct வேண்டாம்"))
chk("combining acute on Latin in hi", clean("hi","cafe","cafe\u0301 खोलें"))
chk("Devanagari nukta/virama/avagraha/om", clean("hi","Om","ॐ ऽ क़ ज़ क्ष"))
chk("Tamil grantha letters", clean("ta","Joshua","ஜோஷ்வா ஸ்ரீ ஹரி"))
chk("Malayalam chillu letters", clean("ml","x","ൺ ൻ ർ ൽ ൾ"))
chk("Kannada/Telugu ok in their targets", clean("kn","x","ಕನ್ನಡ") and clean("te","x","తెలుగు"))
chk("Arabic-Indic digits are not letters", clean("hi","12","\u0661\u0662"))
chk("Japanese brand in source allowed in output", clean("ta","Visit 東京 now","இப்போது 東京 செல்லவும்"))
chk("source Han does not excuse an unrelated Arabic leak", flag("ta","Visit 東京 now","இப்போது 東京 ڊائن"))
chk("Hinglish source Latin only: Meitei still flagged", flag("hi","Open the Settings page","Settings page खोलें ꯃ"))
chk("wrong Indic script for target flagged (Bengali in hi)", flag("hi","x","ক्ष ক"))
chk("empty output not flagged", clean("ta","x",""))
print(f"{ok} pass {bad} fail"); sys.exit(1 if bad else 0)
