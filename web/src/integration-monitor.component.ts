import { Component, Input, Output, EventEmitter } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Operations, OutboxEvent } from "./models";
import { label } from "./workflow-view";
@Component({
  selector: "integration-monitor",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./integration-monitor.component.html",
})
export class IntegrationMonitorComponent {
  @Input() ops: Operations | null = null;
  @Input() busy = false;
  @Output() retry = new EventEmitter<number>();
  @Output() simulate = new EventEmitter<void>();
  status = "All";
  search = "";
  selectedId: number | null = null;
  get filteredEvents() {
    const query = this.search.trim().toLowerCase();
    return (this.ops?.events ?? []).filter(
      (e) =>
        (this.status === "All" || e.status === this.status) &&
        `${e.id} ${e.claimId} ${e.type} ${e.lastError ?? ""}`
          .toLowerCase()
          .includes(query),
    );
  }
  get selected(): OutboxEvent | undefined {
    return this.ops?.events.find((e) => e.id === this.selectedId);
  }
  pretty(payload: string) {
    try {
      return JSON.stringify(JSON.parse(payload), null, 2);
    } catch {
      return payload;
    }
  }
  label = label;
}
