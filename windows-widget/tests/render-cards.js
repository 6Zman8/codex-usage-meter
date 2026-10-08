// Background-only layout check using Microsoft's Widgets host configuration and renderer.
// Inputs: repository root, preview directory. No account/profile data is read.
const fs=require('fs'),path=require('path'),cp=require('child_process');
const root=path.resolve(process.argv[2]||'.');
const output=path.resolve(process.argv[3]||path.join(root,'work/v1.6.0/widget-provider/previews'));
const tools=path.join(root,'obj/widget-tools');
const renderer=fs.readFileSync(path.join(tools,'adaptivecards/package/dist/adaptivecards.js'),'utf8');
const style=fs.readFileSync(path.join(tools,'widget-container-dark.css'),'utf8');
const config=JSON.parse(fs.readFileSync(path.join(tools,'widget-dark.json'),'utf8'));
const cards=['small','medium','large'].map(size=>({size,card:JSON.parse(fs.readFileSync(path.join(output,size+'.json'),'utf8'))}));
const script=`const cards=${JSON.stringify(cards)},config=${JSON.stringify(config)};
AdaptiveCards.AdaptiveCard.onProcessMarkdown=(text,result)=>{result.outputHtml=text.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;');result.didProcess=true;};
let reports=[];for(const item of cards){const card=new AdaptiveCards.AdaptiveCard();card.hostConfig=new AdaptiveCards.HostConfig(config);card.parse(item.card);const section=document.createElement('section');section.className='widget-outer-container widget-'+item.size+'-container';const view=card.render();section.appendChild(view);document.getElementById('cards').appendChild(section);reports.push({size:item.size,hostHeight:{small:146,medium:304,large:462}[item.size],cardHeight:view.getBoundingClientRect().height,validation:card.validateProperties().validationEvents.map(x=>x.message),clippedText:[...view.querySelectorAll('.ac-textBlock')].filter(x=>x.scrollWidth>x.clientWidth+1).map(x=>x.textContent)});}document.getElementById('results').textContent=JSON.stringify(reports);`;
const html='<!doctype html><meta charset="utf-8"><style>'+style+'body{background:#151515;color:white;font-family:Segoe UI;margin:12px}#cards{display:flex;gap:12px}#results{white-space:pre-wrap}</style><div id="cards"></div><pre id="results"></pre><script>'+renderer.replaceAll('</script','<\\/script')+'</script><script>'+script+'</script>';
const file=path.join(output,'rendered.html');fs.writeFileSync(file,html);
const chrome=path.join(process.env.ProgramFiles,'Google/Chrome/Application/chrome.exe');
const result=cp.spawnSync(chrome,['--headless=new','--no-first-run','--no-default-browser-check','--disable-gpu','--user-data-dir='+path.join(output,'chrome-profile'),'--screenshot='+path.join(output,'cards.png'),'--window-size=1000,620','--dump-dom','--virtual-time-budget=500',new URL('file:///'+file.replaceAll('\\','/')).href],{windowsHide:true,encoding:'utf8',timeout:30000,maxBuffer:10000000});
fs.writeFileSync(path.join(output,'chrome.log'),result.stderr||'');
if(result.error)throw result.error;
const match=result.stdout.match(/<pre id="results">([^<]+)<\/pre>/);
if(!match)throw new Error('Renderer produced no result; Chrome exit '+result.status);
const reports=JSON.parse(match[1].replaceAll('&quot;','"').replaceAll('&amp;','&'));
fs.writeFileSync(path.join(output,'layout-results.json'),JSON.stringify(reports,null,2));
const passed=reports.every(x=>x.cardHeight<=x.hostHeight && x.validation.length===0 && x.clippedText.length===0);
console.log(JSON.stringify({passed,reports}));process.exitCode=passed?0:1;
