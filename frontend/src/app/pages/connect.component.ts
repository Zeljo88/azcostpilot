import { CommonModule } from '@angular/common';
import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  ApiService,
  AzureConnectionSummary,
  ConnectedSubscription,
  ConnectAzureResponse
} from '../services/api.service';

@Component({
  selector: 'app-connect',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './connect.component.html',
  styleUrl: './connect.component.css'
})
export class ConnectComponent {
  email = 'demo@azcostpilot.local';
  password = 'Pass@word123';
  tenantId = '';
  clientId = '';
  clientSecret = '';
  saving = false;
  error = '';
  success = '';
  warning = '';
  connections: AzureConnectionSummary[] = [];
  discoveredSubscriptions: ConnectedSubscription[] = [];
  subscriptionCount = 0;

  constructor(private readonly api: ApiService) {}

  async saveConnection(): Promise<void> {
    this.error = '';
    this.success = '';
    this.warning = '';
    this.saving = true;

    try {
      await this.ensureUserAndToken();
      const response: ConnectAzureResponse = await firstValueFrom(
        this.api.saveAzureConnection(this.tenantId.trim(), this.clientId.trim(), this.clientSecret.trim())
      );
      this.connections = await firstValueFrom(this.api.getAzureConnections());
      this.discoveredSubscriptions = response.subscriptions;
      this.subscriptionCount = response.subscriptionCount;
      this.success = `Connected ✅ Found ${this.subscriptionCount} subscription(s). Stored ${this.connections.length} connection(s).`;
      if (response.backfillCompleted) {
        this.success = `${this.success} ${response.backfillMessage}`;
      } else {
        this.warning = response.backfillMessage;
      }
      this.clientSecret = '';
    } catch (error: any) {
      this.error = error?.error?.message ?? 'Could not validate Azure connection.';
    } finally {
      this.saving = false;
    }
  }

  private async ensureUserAndToken(): Promise<void> {
    try {
      await firstValueFrom(this.api.register(this.email.trim(), this.password));
    } catch (error: any) {
      if (error?.status !== 409) {
        throw error;
      }
    }

    const auth = await firstValueFrom(this.api.login(this.email.trim(), this.password));
    this.api.setToken(auth.token);
  }
}
