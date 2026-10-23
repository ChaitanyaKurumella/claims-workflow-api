#!/usr/bin/env python3
"""Reproducible authenticated page-read load probe. Use a separate synthetic database."""
import argparse,concurrent.futures,json,os,platform,statistics,time,urllib.request,urllib.error,uuid
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--url',default='http://localhost:5091');p.add_argument('--seed',type=int,default=1000);p.add_argument('--requests',type=int,default=1000);p.add_argument('--concurrency',type=int,default=8);p.add_argument('--output',default='load-results.json');a=p.parse_args()
if not 1<=a.concurrency<=64 or a.requests<1 or a.seed<0:p.error('invalid workload')
key=os.environ['CLAIMDESK_LOAD_KEY'];run=str(uuid.uuid4())
def request(path,body=None,request_key=None):
    headers={'X-Api-Key':key,'Content-Type':'application/json'}
    if request_key:headers['Idempotency-Key']=request_key
    r=urllib.request.Request(a.url+path,data=None if body is None else json.dumps(body).encode(),headers=headers)
    with urllib.request.urlopen(r,timeout=30) as response:return json.load(response)
for i in range(a.seed):request('/api/claims',{'reference':f'LOAD-{run}-{i}','amount':125.50},f'{run}-{i}')
for _ in range(20):request('/api/claims/page?limit=50')
def probe(i):
    start=time.perf_counter()
    try:request('/api/claims/page?limit=50');status=200
    except urllib.error.HTTPError as e:status=e.code
    except Exception:status=0
    return (time.perf_counter()-start)*1000,status
start=time.perf_counter()
with concurrent.futures.ThreadPoolExecutor(max_workers=a.concurrency) as pool:results=list(pool.map(probe,range(a.requests)))
elapsed=time.perf_counter()-start;latencies=sorted(x[0] for x in results)
report={'workload':'authenticated first-page reads, 50 claims; 20 warmups; sequential seed excluded','seeded_claims':a.seed,'requests':a.requests,'concurrency':a.concurrency,'duration_seconds':round(elapsed,3),'requests_per_second':round(a.requests/elapsed,2),'p50_ms':round(statistics.median(latencies),2),'p95_ms':round(latencies[max(0,int(len(latencies)*.95)-1)],2),'p99_ms':round(latencies[max(0,int(len(latencies)*.99)-1)],2),'errors':sum(s!=200 for _,s in results),'host':platform.platform(),'cpu':platform.machine(),'recorded_at_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
with open(a.output,'w') as f:json.dump(report,f,indent=2);f.write('\n')
print(json.dumps(report,indent=2))
if report['errors']:raise SystemExit(1)
