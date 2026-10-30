const { chromium } = require("playwright-core");
(async()=>{
 const b = await chromium.launch({executablePath:process.env.CHROME_EXE,args:["--no-sandbox","--disable-dev-shm-usage"]});
 const shots=[["boot","widget.html",null],["online","widget.html","hello there"],["validation","widget.html","this is too long"],["unreachable","widget.html?dead=1","anyone there"],["abort","widget.html?stream=1&fail=1","will break"]];
 for(const [n,u,msg] of shots){
  const p=await b.newPage({viewport:{width:420,height:660}});
  await p.goto("http://127.0.0.1:8903/"+u,{waitUntil:"networkidle"});
  await p.waitForFunction(()=>window.__booted===true,null,{timeout:30000});
  await p.waitForSelector("pix-chat-widget input");
  if(msg){await p.fill("pix-chat-widget input",msg);await p.click("pix-chat-widget button[type=submit]");await p.waitForTimeout(1500);}
  await p.screenshot({path:`/downloads/widget-${n}.png`});await p.close();
 }
 await b.close();
})();
