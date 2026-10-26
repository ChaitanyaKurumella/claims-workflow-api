import { label, canDecide } from "./workflow-view";
import { Component, OnInit } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Claim, Audit, Operations, Session, ClaimPage } from "./models";
import { ApiClient, ApiError } from "./api-client";
import { CaseWorkspaceComponent } from "./case-workspace.component";
import { IntegrationMonitorComponent } from "./integration-monitor.component";
@Component({
  selector: "app-root",
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    CaseWorkspaceComponent,
    IntegrationMonitorComponent,
  ],
  templateUrl: "./app.component.html",
})
export class AppComponent implements OnInit {
  private transport = new ApiClient();
  nextCursor: string | null = null;
  key = "";
  actor = "";
  role = "";
  demo = false;
  roles = ["Reviewer", "Supervisor", "Administrator"];
  view = "workspace";
  claims: Claim[] = [];
  selected: Claim | null = null;
  events: Audit[] = [];
  ops: Operations | null = null;
  query = "";
  filter = "All";
  filters = [
    "All",
    "Submitted",
    "InReview",
    "PendingSupervisor",
    "Approved",
    "Rejected",
  ];
  busy = false;
  message = "";
  error = false;
  showCreate = false;
  note = "";
  requestKey = crypto.randomUUID();
  draft = {
    reference: "",
    amount: 1250,
    category: "Medical",
    priority: "Normal",
    description: "",
  };
  designs = [
    [
      "01 / CORRECTNESS",
      "One transaction. Three guarantees.",
      "Claim state, audit record, and delivery event commit together. A failure rolls back the entire operation. Unique references prevent duplicates.",
    ],
    [
      "02 / CONCURRENCY",
      "Review the version you decide.",
      "Every command includes the version seen by the reviewer. Concurrent changes return a conflict and require a fresh review. SQLite serializes writers across processes.",
    ],
    [
      "03 / GOVERNANCE",
      "Independent approval above $10,000.",
      "The first reviewer requests supervisor sign-off. A different actor with a supervisor role performs the final decision. Final claims cannot transition again.",
    ],
    [
      "04 / RELIABILITY",
      "Failures become visible work.",
      "Delivery retries use exponential backoff. Five failures move an event to dead letter. Supervisors can replay it. The local sink deduplicates by event ID.",
    ],
    [
      "05 / OPERATIONS",
      "Time is part of the workflow.",
      "Urgent claims have a four-hour deadline; normal claims have 24 hours. The worker marks overdue active claims, increments their version, and records an escalation event.",
    ],
    [
      "06 / TRADEOFFS",
      "A focused modular application.",
      "A single API and worker avoid unnecessary distributed coordination. SQLite serializes writers on one host. Indexed relational pages bound register reads. Optional HTTP delivery uses leases and consumer deduplication.",
    ],
  ];
  async ngOnInit() {
    try {
      this.demo = (await (await fetch("/demo/config")).json()).enabled;
      await this.refreshAll();
    } catch {}
  }
  label = label;
  count(s: string) {
    return this.claims.filter((c) => c.status === s).length;
  }
  final(c: Claim) {
    return ["Approved", "Rejected"].includes(c.status);
  }
  get exposure() {
    return this.claims
      .filter((c) => !this.final(c))
      .reduce((a, c) => a + c.amount, 0);
  }
  get openCount() {
    return this.claims.filter((c) => !this.final(c)).length;
  }
  get visible() {
    return this.claims.filter(
      (c) =>
        (this.filter === "All" || c.status === this.filter) &&
        (c.reference + " " + c.category)
          .toLowerCase()
          .includes(this.query.toLowerCase()),
    );
  }
  overdue(c: Claim) {
    return !this.final(c) && Date.parse(c.dueAt) < Date.now();
  }
  canDecide(c: Claim) {
    return canDecide(c, this.actor, this.role);
  }
  async api<T>(
    path: string,
    body?: unknown,
    extra: Record<string, string> = {},
  ): Promise<T> {
    try {
      return await this.transport.request<T>(path, this.key, body, extra);
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        this.actor = "";
        this.role = "";
        this.claims = [];
        this.nextCursor = null;
        this.clearSelection();
        this.ops = null;
        this.showCreate = false;
        this.view = "workspace";
      }
      throw e;
    }
  }
  async run(f: () => Promise<void>) {
    this.busy = true;
    this.error = false;
    try {
      await f();
    } catch (e) {
      this.error = true;
      this.message = e instanceof Error ? e.message : "Request failed.";
    } finally {
      this.busy = false;
    }
  }
  loadMore() {
    return this.run(async () => {
      if (!this.nextCursor) return;
      const page = await this.api<ClaimPage>(
        "/api/claims/page?after=" + encodeURIComponent(this.nextCursor),
      );
      const known = new Set(this.claims.map((c) => c.id));
      this.claims = [
        ...this.claims,
        ...page.items.filter((c) => !known.has(c.id)),
      ];
      this.nextCursor = page.nextCursor;
    });
  }
  clearSelection() {
    this.selected = null;
    this.events = [];
    this.note = "";
  }
  async refresh() {
    const selectedId = this.selected?.id;
    this.clearSelection();
    const page = await this.api<ClaimPage>("/api/claims/page");
    this.claims = page.items;
    this.nextCursor = page.nextCursor;
    if (selectedId) {
      try {
        const claim = await this.api<Claim>("/api/claims/" + selectedId);
        const events = await this.api<Audit[]>(
          "/api/claims/" + selectedId + "/history",
        );
        this.selected = claim;
        this.events = events;
      } catch (e) {
        if (!(e instanceof ApiError) || e.status !== 404) throw e;
        this.message =
          "This claim is no longer available. Select a claim from the refreshed register.";
      }
    }
  }
  refreshAll() {
    return this.run(async () => {
      const s = await this.api<Session>("/api/session");
      this.actor = s.id;
      this.role = s.role;
      await this.refresh();
      if (this.role !== "Reviewer")
        this.ops = await this.api("/api/operations");
    });
  }
  connect() {
    return this.refreshAll();
  }
  demoLogin(role: string) {
    return this.run(async () => {
      await this.api("/demo/session/" + role, {});
      this.key = "";
      const s = await this.api<Session>("/api/session");
      this.actor = s.id;
      this.role = s.role;
      await this.refresh();
      if (role === "Reviewer" && this.view === "operations")
        this.view = "workspace";
      this.message = "Demo perspective: " + role;
    });
  }
  create() {
    return this.run(async () => {
      const c = await this.api<Claim>("/api/claims", this.draft, {
        "Idempotency-Key": this.requestKey,
      });
      this.requestKey = crypto.randomUUID();
      this.draft = {
        reference: "",
        amount: 1250,
        category: "Medical",
        priority: "Normal",
        description: "",
      };
      this.showCreate = false;
      await this.refresh();
      this.selected = c;
      this.events = await this.api("/api/claims/" + c.id + "/history");
      this.message = "Claim submitted with an audit record and delivery event.";
    });
  }
  select(c: Claim) {
    return this.run(async () => {
      this.clearSelection();
      const claim = await this.api<Claim>("/api/claims/" + c.id);
      const events = await this.api<Audit[]>(
        "/api/claims/" + c.id + "/history",
      );
      this.selected = claim;
      this.events = events;
    });
  }
  act(action: string) {
    return this.run(async () => {
      if (!this.selected) return;
      try {
        await this.api("/api/claims/" + this.selected.id + "/" + action, {
          version: this.selected.version,
          note: this.note,
        });
        this.note = "";
        this.message = "Workflow updated.";
      } finally {
        await this.refresh();
      }
    });
  }
  openOperations() {
    this.view = "operations";
    return this.run(async () => {
      this.ops = await this.api("/api/operations");
    });
  }
  retry(id: number) {
    return this.run(async () => {
      await this.api("/api/operations/outbox/" + id + "/retry", {});
      this.ops = await this.api("/api/operations");
      this.message = "Event returned to the delivery queue.";
    });
  }
}
