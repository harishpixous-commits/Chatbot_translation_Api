const { chromium } = require("playwright-core");
(async()=>{
 const b = await chromium.launch({executablePath:process.env.CHROME_EXE,args:["--no-sandbox","--disable-dev-shm-usage"]});
 const p=await b.newPage({viewport:{width:420,height:660}});
 await p.goto("http://127.0.0.1:8903/widget.html",{waitUntil:"networkidle"});
 await p.waitForFunction(()=>window.__booted===true,null,{timeout:30000});
 await p.fill("pix-chat-widget input","withheld case");await p.click("pix-chat-widget button[type=submit]");await p.waitForTimeout(1500);
 await p.screenshot({path:"/downloads/widget-withheld.png"});await b.close();
})();
