import { Component } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
interface Claim { id:string; reference:string; amount:number; status:string; }
interface Audit { at:string; action:string; }
@Component({selector:'app-root',standalone:true,imports:[CommonModule,FormsModule],template:`
<header><a href="https://github.com/ChaitanyaKurumella/claims-workflow-api">CK / ENGINEERING LAB</a><span>Independent portfolio · Synthetic data</span></header>
<main><p class="eyebrow">OPERATIONS WORKSPACE</p><h1>Every claim.<br><em>A clear decision.</em></h1><p class="intro">Submit, review, and trace a claim through its lifecycle.</p>
<section class="connection"><label>Local API key <input type="password" [(ngModel)]="key" autocomplete="off" placeholder="Enter the key configured on your server"></label><button (click)="load()" [disabled]="busy || !key">Connect & refresh</button><button class="secondary" (click)="disconnect()">Disconnect</button></section>
<p role="status" class="message">{{message}}</p>
<div class="metrics"><div><strong>{{claims.length}}</strong><span>Total claims</span></div><div><strong>{{count('Submitted')}}</strong><span>Awaiting review</span></div><div><strong>{{count('Approved')}}</strong><span>Approved</span></div><div><strong>{{count('Rejected')}}</strong><span>Rejected</span></div></div>
<div class="workspace"><section class="panel"><p class="eyebrow">01 / SUBMIT</p><h2>New claim</h2><form (ngSubmit)="create()"><label>Reference<input name="reference" [(ngModel)]="reference" required maxlength="64" placeholder="DEMO-001"></label><label>Amount · demo units<input name="amount" type="number" [(ngModel)]="amount" min="0.01" max="1000000" step="0.01" required></label><button [disabled]="busy || !key || !reference.trim() || amount <= 0">Submit claim</button></form><p class="note">Use synthetic references only. Final decisions cannot be reversed.</p></section>
<section class="panel queue"><div class="section-head"><div><p class="eyebrow">02 / REVIEW</p><h2>Claims queue</h2></div><label>Filter<select [(ngModel)]="filter"><option>All</option><option>Submitted</option><option>Approved</option><option>Rejected</option></select></label></div>
<div class="table-wrap"><table><thead><tr><th>Reference</th><th>Amount</th><th>Status</th><th>Review</th></tr></thead><tbody><tr *ngFor="let c of visible"><td><button class="text" (click)="history(c)">{{c.reference}}</button></td><td>{{c.amount | number:'1.2-2'}}</td><td><span class="badge" [attr.data-status]="c.status">{{c.status}}</span></td><td><div class="actions" *ngIf="c.status === 'Submitted'"><button [disabled]="busy" (click)="decide(c,'approve')">Approve</button><button class="secondary" [disabled]="busy" (click)="decide(c,'reject')">Reject</button></div><span *ngIf="c.status !== 'Submitted'">Final</span></td></tr></tbody></table></div><p class="note" *ngIf="!visible.length">No claims to display. Connect and submit a synthetic claim to begin.</p></section></div>
<section class="panel" *ngIf="selected"><p class="eyebrow">03 / TRACE</p><h2>{{selected}} · Audit history</h2><ol><li *ngFor="let e of events"><strong>{{e.action}}</strong> <time>{{e.at | date:'medium'}}</time></li></ol></section>
<footer>ASP.NET Core + Angular · Single-process durable storage · Explicit workflow rules</footer></main>`})
class App {
 key=''; claims:Claim[]=[]; reference=''; amount=125.5; filter='All'; busy=false; message='Connect to your local API to start.'; selected=''; events:Audit[]=[];
 get visible(){ return this.claims.filter(c=>this.filter==='All'||c.status===this.filter); }
 count(status:string){ return this.claims.filter(c=>c.status===status).length; }
 async api(path:string,body?:unknown){
  const r=await fetch('/api/claims'+path,{method:body?'POST':'GET',headers:{'X-Api-Key':this.key,'Content-Type':'application/json'},...(body?{body:JSON.stringify(body)}:{})});
  if(!r.ok){const e=await r.json().catch(()=>({}));throw Error(r.status===401?'Access denied. Check your API key.':e.detail||'Request failed ('+r.status+').');} return r.json();
 }
 async run(action:()=>Promise<void>){this.busy=true;try{await action();}catch(e){this.message=e instanceof Error?e.message:'Request failed.';}finally{this.busy=false;}}
 async refresh(){this.claims=await this.api('');}
 load(){return this.run(async()=>{await this.refresh();this.message='Connected. Queue refreshed.';});}
 create(){return this.run(async()=>{await this.api('',{reference:this.reference,amount:this.amount});this.reference='';await this.refresh();this.message='Claim submitted.';});}
 decide(c:Claim,outcome:string){return this.run(async()=>{await this.api('/'+c.id+'/decision',{outcome});await this.refresh();if(this.selected===c.reference)this.events=await this.api('/'+c.id+'/history');this.message='Decision recorded.';});}
 history(c:Claim){return this.run(async()=>{this.events=await this.api('/'+c.id+'/history');this.selected=c.reference;this.message='Audit history loaded.';});}
 disconnect(){this.key='';this.claims=[];this.events=[];this.selected='';this.message='Disconnected. Key cleared from this view.';}
}
bootstrapApplication(App).catch(console.error);
