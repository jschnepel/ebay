// Runs the same user-visible actions against a disposable stack and its staging deployment.
const { chromium, expect } = require(require.resolve('@playwright/test', { paths: [process.env.ROLE_BROWSER_MODULES || __dirname] }));
const { randomUUID } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const base = new URL(process.env.AUCTION_BASE_URL);
const run = randomUUID().slice(0, 8);
const out = process.env.ROLE_RESULTS || 'role-results';
const steps = [];
const title = label => `QA ${run} ${label}`;
const button = (page, name) => page.getByRole('button', { name, exact: true });
const link = (page, name) => page.getByRole('link', { name, exact: true });
const row = (page, id, name) => page.getByTestId(id).filter({ hasText: name });
const go = (page, route) => page.goto(new URL(route.replace(/^\//, ''), base).href);
const signUp = async (page, label) => {
  const email = `${label}-${run}@example.test`;
  const response = await go(page, 'sign-in');
  const displayName = page.getByLabel('Display name', {exact: true});
  try {
    await displayName.waitFor({state: 'visible', timeout: 30000});
  } catch (error) {
    const finalUrl = new URL(page.url());
    const details = {
      status: response?.status(), origin: finalUrl.origin, path: finalUrl.pathname,
      title: (await page.title()).slice(0, 150),
      headings: (await page.getByRole('heading').allTextContents()).slice(0, 4).map(s=>s.slice(0, 100)),
    };
    console.log('Sign-up page: '+JSON.stringify(details));
    fs.writeFileSync(path.join(out,'signup-page.json'), JSON.stringify(details,null,2));
    throw error;
  }
  await displayName.fill(title(label));
  await page.getByLabel('Email for the new account').fill(email);
  await button(page, 'Create account').click();
  await expect(page.getByTestId('signed-in-as')).toHaveText(title(label));
  return email;
};
async function signIn(page, email, password = '') {
  await go(page, 'sign-in');
  await page.getByLabel('Email', { exact: true }).fill(email);
  if (password) await page.getByLabel('Password (seeded accounts only)').fill(password);
  await button(page, 'Sign in').click();
}
async function bid(page, amount) {
  await page.getByLabel(/^Your (maximum|bid)/).fill(amount);
  await page.getByRole('button', {name: /^(Place bid|Accepted|Rejected .*place bid again)$/i}).click();
}
async function select(page, label, option) {
  await page.getByLabel(label, { exact: true }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}
async function saveDraft(page, name, {floor, entry, start, end} = {}) {
  await go(page, 'sell/new');
  await page.getByLabel('Title', {exact:true}).fill(name);
  await page.getByLabel('Description', {exact:true}).fill('Disposable browser walkthrough.');
  await page.getByLabel('Opening price ($)').fill('10.00');
  if (floor) await select(page, 'Price floor', floor);
  else await page.getByLabel('Reserve ($)').fill('15.00');
  if (entry) await select(page, 'Bidding', entry);
  if (start) await page.getByLabel('Starts', {exact:true}).fill(start);
  if (end) await page.getByLabel('Ends', {exact:true}).fill(end);
  const result = page.waitForResponse(res => res.request().method()==='POST' && /\/auctions(?:\?|$)/.test(res.url()), {timeout: 10000}).catch(()=>null);
  await button(page, 'Save draft').click();
  const item = row(page, 'my-auction-row', name);
  try {
    await expect(item).toContainText('Draft',{timeout:10000});
  } catch (error) {
    const res = await result;
    const problem = res && !res.ok() ? await res.json().catch(()=>null) : null;
    const message = await page.getByTestId('draft-error').allTextContents();
    throw new Error('Draft '+name+' did not appear. '+JSON.stringify({status:res?.status(),errors:problem?.errors,message})+' '+error.message);
  }
  return item;
}
async function publish(page, name, options, status = 'Active') {
  const item = await saveDraft(page, name, options);
  await button(item, 'Publish').click();
  await expect(item).toContainText(status);
  return new URL(await link(item, name).getAttribute('href'), base).href;
}
async function step(name, page, fn) {
  const started = Date.now();
  try {
    await fn();
    steps.push({name, status:'passed', ms: Date.now()-started});
  } catch (e) {
    const error = String(e.message).replace(/\x1b\[[\d;]*m/g,'').slice(0,1600);
    steps.push({name, status:'failed', ms: Date.now()-started, error});
    // A sign-in page may contain an email or a password field.
    if (!/\/(sign-in|login)(\?|$)/.test(page.url()))
      await page.screenshot({path:path.join(out, `failure-${steps.length}.png`)}).catch(()=>{});
  }
  console.log(`${steps.at(-1).status.toUpperCase()}  ${name}`);
  if (steps.at(-1).error) console.log(steps.at(-1).error);
  fs.writeFileSync(path.join(out,'walkthrough.json'), JSON.stringify(steps,null,2));
}
async function main() {
  fs.mkdirSync(out,{recursive:true});
  const browser = await chromium.launch();
  try {
    const newPage = async () => {
      const context = await browser.newContext({timezoneId:'UTC', httpCredentials: process.env.QA_GATE_PASSWORD ?
        {username:'walkthrough',password:process.env.QA_GATE_PASSWORD,origin:base.origin} : undefined});
      const page = await context.newPage();
      page.setDefaultTimeout(12000);
      return page;
    };
    const [fixtures,buyer,rival,seller,admin] = await Promise.all(Array.from({length:5},newPage));
    const lot=title('camera'), target=title('moderation'), maker=title('seller');
    await signUp(fixtures,'fixtures');
    const auction=await publish(fixtures,lot);
    const fixed=await publish(fixtures,title('fixed'),{floor:'No reserve',entry:'Fixed steps of one increment'});
    const end=new Date(Date.now()+240000).toISOString().slice(0,16);
    let sale, reserveLot, noBids;
    await step('Setup / closing auctions',fixtures,async()=>{
      sale=await publish(fixtures,title('sale'),{end,floor:'Minimum bid (the opening price)'});
      reserveLot=await publish(fixtures,title('reserve'),{end});
      noBids=await publish(fixtures,title('no-bids'),{end});
    });
    // Each mutation targets a listing or account created by this run.
    let email, sellerEmail, sellerAuction;
    const b=(name,fn)=>step('Buyer / '+name,buyer,fn);
    const s=(name,fn)=>step('Seller / '+name,seller,fn);
    const a=(name,fn)=>step('Admin / '+name,admin,fn);
    await b('Guest browse, search, filter, open details and sign-in prompt',async()=>{
      await go(buyer,''); await buyer.getByLabel('Search titles').fill(lot);
      await select(buyer,'Show',/^(Live now|Active)$/); await button(buyer,'Search').click();
      await expect(buyer.getByTestId('auction-row')).toHaveCount(1);
      await link(buyer,lot).click();
      await expect(buyer.getByTestId('current-price')).toContainText('$10.00');
      await expect(buyer.getByText(/sign in to bid/i)).toBeVisible();
    });
    await b('Create account; empty My bids and notifications',async()=>{
      email=await signUp(buyer,'buyer'); await link(buyer,'My bids').click();
      await expect(buyer.getByText(/haven't bid on anything/)).toBeVisible();
      await buyer.getByTestId('bell').click(); await expect(buyer.getByText(/Nothing yet/)).toBeVisible();
      await go(buyer,'admin/users'); await expect(buyer.getByTestId('state-forbidden')).toBeVisible();
    });
    await b('Place bids for closing outcomes',async()=>{
      await buyer.goto(sale); await bid(buyer,'25'); await expect(buyer.getByText('You are the highest bidder.',{exact:true})).toBeVisible();
      await buyer.goto(reserveLot); await bid(buyer,'12'); await expect(buyer.getByText('You are the highest bidder.',{exact:true})).toBeVisible();
    });
    await b('Invalid and low bid, then accepted proxy maximum and reserve price',async()=>{
      await buyer.goto(auction); await bid(buyer,'oops'); await expect(buyer.getByTestId('bid-result')).toContainText('Enter an amount');
      await bid(buyer,'1'); await expect(buyer.getByTestId('bid-result')).toContainText(/minimum/i);
      await bid(buyer,'25'); await expect(buyer.getByText('You are the highest bidder.',{exact:true})).toBeVisible();
      await expect(buyer.getByTestId('current-price')).toHaveText('$15.00');
      await expect(buyer.getByText('Your reserve',{exact:true})).toHaveCount(0);
      await buyer.reload(); await expect(buyer.getByText('You are the highest bidder.',{exact:true})).toBeVisible();
    });
    await b('Outbid, read bell, open history, check My bids',async()=>{
      await signUp(rival,'rival'); await rival.goto(auction); await bid(rival,'35');
      await expect(rival.getByText('You are the highest bidder.',{exact:true})).toBeVisible();
      await expect(buyer.getByTestId('current-price')).toHaveText('$26.00');
      await buyer.getByTestId('bell').click();
      const notice=row(buyer,'notification',lot); await expect(notice).toContainText('outbid'); await notice.click();
      await expect(buyer.getByRole('heading',{name:'Bid history'})).toBeVisible();
      await expect(buyer.getByTestId('unread-count')).toHaveText('0');
      await link(buyer,'My bids').click(); await expect(row(buyer,'my-bid-row',lot)).toContainText('Outbid');
    });
    await b('Fixed bid, wrong step, valid next step',async()=>{
      await buyer.goto(fixed); await bid(buyer,'10'); await expect(buyer.getByTestId('current-price')).toHaveText('$10.00');
      await bid(buyer,'12'); await expect(buyer.getByTestId('bid-result')).toContainText(/next step/i);
      await bid(buyer,'11'); await expect(buyer.getByTestId('current-price')).toHaveText('$11.00');
    });
    await b('Sign out, reject protected view, sign back in with history intact',async()=>{
      await button(buyer,'Sign out').click(); await go(buyer,'my-bids');
      await expect(buyer.getByTestId('state-forbidden')).toBeVisible();
      await signIn(buyer,email); await expect(buyer.getByTestId('signed-in-as')).toHaveText(title('buyer'));
      await link(buyer,'My bids').click(); await expect(row(buyer,'my-bid-row',lot)).toContainText('Outbid');
    });
    await b('Wait through the deadline; inspect sold, reserve not met and no-bid results',async()=>{
      if (!sale || !reserveLot || !noBids) throw new Error('Closing fixtures were unavailable.');
      for(const [url,outcome] of [[sale,'sold'],[reserveLot,'reserve not met'],[noBids,'no bids']]){
        await buyer.goto(url);
        await expect(buyer.getByTestId('status')).toHaveText(new RegExp('Closed.*'+outcome,'i'),{timeout:260000});
        await expect(button(buyer,'Place bid')).toHaveCount(0);
      }
      await buyer.getByTestId('bell').click();
      await expect(row(buyer,'notification',title('sale')).first()).toContainText('You won');
      await expect(row(buyer,'notification',title('reserve')).first()).toContainText('below its reserve');
      await go(buyer,''); await buyer.getByLabel('Search titles').fill(title('no-bids'));
      await select(buyer,'Show','Closed'); await button(buyer,'Search').click(); await expect(row(buyer,'auction-row',title('no-bids'))).toBeVisible();
    });
    await s('Create seller, empty listings, validation on invalid money',async()=>{
      sellerEmail=await signUp(seller,'seller'); await link(seller,'Sell').click();
      await expect(seller.getByText('You have no listings yet.')).toBeVisible();
      await link(seller,'New listing').click(); await seller.getByLabel('Opening price ($)').fill('invalid');
      await button(seller,'Save draft').click(); await expect(seller.getByTestId('draft-error')).toContainText('dollars');
    });
    await s('Read seller closing notices for sale, reserve not met and no bids',async()=>{
      if (!sale || !reserveLot || !noBids) throw new Error('Closing fixtures were unavailable.');
      await fixtures.getByTestId('bell').click();
      await expect(row(fixtures,'notification',title('sale')).first()).toContainText('ended and sold');
      await expect(row(fixtures,'notification',title('reserve')).first()).toContainText('below its reserve');
      await expect(row(fixtures,'notification',title('no-bids')).first()).toContainText('no bids');
    });
    await s('Save private draft, edit title and description, then delete',async()=>{
      const first=title('private'); const renamed=title('renamed'); let item=await saveDraft(seller,first);
      const url=new URL(await link(item,first).getAttribute('href'),base).href;
      await buyer.goto(url); await expect(buyer.getByTestId('state-error')).toContainText("doesn't exist");
      await link(item,'Edit').click(); await seller.getByLabel('Title',{exact:true}).fill(renamed);
      await seller.getByLabel('Description',{exact:true}).fill('Updated description');
      await button(seller,'Save draft').click(); item=row(seller,'my-auction-row',renamed);
      await expect(item).toContainText('Draft'); await button(item,'Delete').click();
      await expect(seller.getByTestId('sell-result')).toContainText('Deleted'); await expect(item).toHaveCount(0);
    });
    await s('Upload images, change cover, reorder, remove, publish, verify buyer gallery',async()=>{
      const item=await saveDraft(seller,target); await link(item,'Edit').click();
      const upload=seller.getByLabel('Add an image');
      await upload.setInputFiles({name:'invalid.png',mimeType:'image/png',buffer:Buffer.from('not a photo')});
      await expect(seller.getByTestId('image-error')).toBeVisible();
      const images=seller.getByTestId('draft-image');
      for(let n=1;n<=4;n++){
        await upload.setInputFiles({name:'photo'+n+'.png',mimeType:'image/png',buffer:photo(n)});
        await expect(images).toHaveCount(n);
      }
      await expect(upload).toHaveCount(0);
      const src=await images.nth(1).locator('img').getAttribute('src');
      await button(images.nth(1),'Make cover').click(); await expect(images.nth(1)).toContainText('Cover');
      await button(images.nth(1),'Move earlier').click(); await expect(images.first().locator('img')).toHaveAttribute('src',src);
      await button(images.first(),'Move later').click(); await expect(images.nth(1).locator('img')).toHaveAttribute('src',src);
      await button(images.nth(3),'Remove').click(); await expect(images).toHaveCount(3);
      await link(seller,'Sell').click(); await button(item,'Publish').click(); await expect(item).toContainText('Active');
      sellerAuction=new URL(await link(item,target).getAttribute('href'),base).href;
      await buyer.goto(sellerAuction); await expect(buyer.getByTestId('auction-image')).toHaveCount(3);
      await go(buyer,''); await buyer.getByLabel('Search titles').fill(target); await button(buyer,'Search').click();
      await expect(row(buyer,'auction-row',target).getByTestId('cover')).toHaveAttribute('src',src);
    });
    await s('Cancel active no-bid listing and a scheduled listing',async()=>{
      for(const scheduled of [false,true]){
        const name=title(scheduled?'scheduled':'unused');
        const iso=offset=>new Date(Date.now()+offset).toISOString().slice(0,16);
        await publish(seller,name,scheduled?{start:iso(3600000),end:iso(7200000)}:{},scheduled?'Scheduled':'Active');
        const item=row(seller,'my-auction-row',name); await button(item,'Cancel').click(); await expect(item).toContainText('Cancelled');
      }
    });
    await s('Published listing cannot be edited, self-bid hidden, first bid removes Cancel',async()=>{
      if(!sellerAuction) sellerAuction=await publish(seller,title('seller-protection'));
      await seller.goto(sellerAuction); await expect(seller.getByTestId('status')).toHaveText('Active');
      await expect(button(seller,'Place bid')).toHaveCount(0); await buyer.goto(sellerAuction); await bid(buyer,'20');
      await expect(buyer.getByText('You are the highest bidder.',{exact:true})).toBeVisible(); await link(seller,'Sell').click();
      const item=row(seller,'my-auction-row',await buyer.getByRole('heading',{level:1}).innerText());
      await expect(item).toContainText('Active'); await expect(item.getByRole('cell').nth(2)).toHaveText('1');
      await expect(button(item,'Cancel')).toHaveCount(0); await expect(link(item,'Edit')).toHaveCount(0);
    });
    if (!process.env.QA_ADMIN_EMAIL || !process.env.QA_ADMIN_PASSWORD) {
      const name = 'Admin / sign in and moderation';
      steps.push({name, status:'blocked', reason:'No dedicated admin credentials for this host.'});
      fs.writeFileSync(path.join(out,'walkthrough.json'), JSON.stringify(steps,null,2));
      console.log('BLOCKED  '+name+' (no dedicated credentials)');
      if (steps.some(x=>x.status==='failed')) process.exitCode=1;
      return;
    }
    const modName=title('admin-lot'); const modAuction=await publish(fixtures,modName);
    await a('Admin sign-in, dashboard health and four latency measures',async()=>{
      await signIn(admin,process.env.QA_ADMIN_EMAIL,process.env.QA_ADMIN_PASSWORD);
      await link(admin,'Activity').click(); await expect(admin.getByTestId('kpi-row')).toHaveCount(4);
      await expect(admin.getByTestId('health-row').first()).toBeVisible();
      await expect(link(admin,'Sell')).toHaveCount(0); await expect(link(admin,'My bids')).toHaveCount(0);
    });
    await a('Find listing; require reason; suspend, resume and cancel in the watching buyer tab',async()=>{
      await buyer.goto(modAuction); await link(admin.getByRole('navigation',{name:'Main'}),'Auctions').click();
      await admin.getByLabel('Title, seller name or id').fill(modName); await button(admin,'Search').click();
      const item=row(admin,'moderated-auction',modName);
      await button(item,'Suspend').click(); await expect(admin.getByTestId('moderation-result')).toContainText('Give a reason');
      await admin.getByLabel(/^Reason/).fill('QA review '+run);
      for(const [action,status] of [['Suspend','Suspended'],['Resume','Active'],['Cancel','Cancelled']]){
        await button(item,action).click(); await expect(item).toContainText(status);
        await expect(buyer.getByTestId('status')).toContainText(status);
      }
      await select(admin,'Status','Cancelled'); await button(admin,'Search').click(); await expect(item).toContainText('Cancelled');
      await fixtures.getByTestId('bell').click(); await expect(row(fixtures,'notification',modName).filter({hasText:/cancelled/i}).first()).toContainText('QA review '+run);
    });
    await a('Search disposable user; require reason; disable, confirm sign-in refusal, enable and recover',async()=>{
      await link(admin,'Users').click(); await admin.getByLabel('Display name or account id').fill(maker);
      await button(admin,'Search').click(); const item=row(admin,'user-row',maker);
      await button(item,'Disable').click(); await expect(admin.getByTestId('user-result')).toContainText('Give a reason');
      await admin.getByLabel(/^Reason/).fill('QA account check '+run); await button(item,'Disable').click();
      await expect(item.getByRole('cell').nth(3)).toHaveText('Disabled');
      try { await signIn(seller,sellerEmail); await expect(seller.getByTestId('sign-in-error')).toBeVisible(); }
      finally { await button(item,'Enable').click(); await expect(item.getByRole('cell').nth(3)).toHaveText('Enabled'); }
      await signIn(seller,sellerEmail); await expect(seller.getByTestId('signed-in-as')).toHaveText(maker);
    });
    await a('Admin sign-out and denial of signed-out admin pages',async()=>{
      await button(admin,'Sign out').click(); await go(admin,'admin/users');
      await expect(admin.getByTestId('state-forbidden')).toBeVisible();
    });
  } finally {await browser.close();}
  if(steps.some(x=>x.status==='failed')) process.exitCode=1;
}
// Valid PNGs with different pixels so normalization and deduplication cannot merge the fixtures.
function photo(n) {
  const zlib=require('node:zlib');
  function chunk(type,data){
    const body=Buffer.concat([Buffer.from(type),data]);let crc=0xffffffff;
    for(const byte of body){crc^=byte;for(let bit=0;bit<8;bit++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}
    const length=Buffer.alloc(4),sum=Buffer.alloc(4);length.writeUInt32BE(data.length);sum.writeUInt32BE((crc^0xffffffff)>>>0);
    return Buffer.concat([length,body,sum]);
  }
  const ihdr=Buffer.alloc(13);ihdr.writeUInt32BE(1);ihdr.writeUInt32BE(1,4);ihdr[8]=8;ihdr[9]=2;
  return Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'),chunk('IHDR',ihdr),
    chunk('IDAT',zlib.deflateSync(Buffer.from([0,40*n,80,140]))),chunk('IEND',Buffer.alloc(0))]);
}
main().catch(e=>{console.error(e.message);process.exitCode=1;});
