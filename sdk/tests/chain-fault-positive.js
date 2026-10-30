const { chromium } = require("playwright-core"); const fs=require("fs");
(async()=>{
 const b=await chromium.launch({executablePath:process.env.CHROME_EXE,args:["--no-sandbox","--disable-dev-shm-usage"]});
 const p=await b.newPage(); await p.goto("http://127.0.0.1:5099/demo.html",{waitUntil:"load"});
 await p.evaluate(()=>{document.body.innerHTML='<h1 id="a">Good morning</h1><p id="b">FAULT sentence here</p><p id="c">Welcome back</p>';});
 await p.addScriptTag({content:fs.readFileSync(process.env.HOME+"/chatbot/sdk/pixous-i18n.js","utf8")});
 await p.evaluate(()=>PixousI18n.init({sourceLanguage:"en",endpoint:"/api/ai/translation/batch"}));
 await p.evaluate(()=>PixousI18n.setLanguage("ta"));
 await p.waitForFunction(()=>["partial","done","error"].includes(document.documentElement.getAttribute("data-pixous-i18n")),null,{timeout:30000});
 console.log("state",await p.evaluate(()=>document.documentElement.getAttribute("data-pixous-i18n")));
 for (const id of ["a","b","c"]) console.log(id,JSON.stringify(await p.textContent("#"+id)));
 await b.close();
})().catch(e=>{console.log("ERR",e.message);process.exit(1)});
