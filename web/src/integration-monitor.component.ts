import { Component, Input, Output, EventEmitter } from "@angular/core";
import { CommonModule } from "@angular/common";
import { Operations } from "./models";
import { label } from "./workflow-view";
@Component({
  selector: "integration-monitor",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./integration-monitor.component.html",
})
export class IntegrationMonitorComponent {
  @Input() ops: Operations | null = null;
  @Input() busy = false;
  @Output() retry = new EventEmitter<number>();
  label = label;
}
