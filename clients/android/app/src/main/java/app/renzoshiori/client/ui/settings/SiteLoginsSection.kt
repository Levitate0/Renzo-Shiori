package app.renzoshiori.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.VpnKey
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.SaveSiteCookieDto
import app.renzoshiori.client.data.model.SaveSiteLoginDto
import app.renzoshiori.client.data.model.SiteCredentialDto
import app.renzoshiori.client.data.model.SiteInfoDto
import app.renzoshiori.client.data.model.SiteLoginResultDto
import app.renzoshiori.client.data.network.SiteAuthApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch

/**
 * Account → Site Logins, transliterated from
 * RenzoFrontend/src/components/comp/settings/site-logins-section.tsx.
 *
 * Logins for coin/paid scanlation sites: the password is stored encrypted
 * server-side, Renzo Shiori logs in for you and re-logs-in when the session
 * lapses; sites that can't be automated take a pasted session cookie. The
 * web's side-by-side Site/Method and Username/Password grids stack vertically
 * here — that's the only intentional difference.
 */
@Composable
fun SiteLoginsSection(snackbar: SnackbarHostState) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()

    var sites by remember { mutableStateOf<List<SiteInfoDto>>(emptyList()) }
    var creds by remember { mutableStateOf<List<SiteCredentialDto>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }

    var provider by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var cookie by remember { mutableStateOf("") }
    var mode by remember { mutableStateOf("password") }
    var busy by remember { mutableStateOf(false) }
    var pendingId by remember { mutableStateOf<String?>(null) }
    var confirmDelete by remember { mutableStateOf<SiteCredentialDto?>(null) }

    suspend fun refresh() {
        val api = app.network.currentServiceOf<SiteAuthApi>() ?: return
        runCatching {
            val s = api.sites()
            val c = api.credentials()
            sites = s
            creds = c
            if (provider.isEmpty() && s.isNotEmpty()) provider = s.first().provider
        }.onFailure { snackbar.showSnackbar(it.apiMessage("Failed to load site logins")) }
        loading = false
    }

    LaunchedEffect(Unit) { refresh() }

    suspend fun report(result: SiteLoginResultDto) {
        snackbar.showSnackbar(
            result.detail ?: if (result.success) "Logged in" else "Login failed",
        )
    }

    val configured = creds.map { it.provider }.toSet()
    val availableSites = sites.filter { it.provider !in configured }

    Column(modifier = Modifier.fillMaxWidth()) {
        Text(
            "Log in to coin/paid scanlation sites so Renzo Shiori can load the chapters you've " +
                "paid for. Your password is encrypted and never shown again; Renzo Shiori logs in " +
                "for you and re-logs-in automatically when the site's session expires. For sites " +
                "that can't be automated (CAPTCHA / Google sign-in), paste a session cookie instead.",
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
        )
        Spacer(Modifier.height(16.dp))

        // ── Existing logins ─────────────────────────────────────────────
        when {
            loading -> LoadingBlock()
            creds.isEmpty() -> EmptyNote("No site logins yet.")
            else -> creds.forEach { credential ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 8.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .border(1.dp, RenzoColors.Border.copy(alpha = 0.5f), RoundedCornerShape(8.dp))
                        .background(RenzoColors.Card.copy(alpha = 0.5f))
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                ) {
                    StatusIcon(credential.status)
                    Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                credential.provider,
                                style = MaterialTheme.typography.titleSmall,
                                color = RenzoColors.Foreground,
                            )
                            Text(
                                "· ${credential.username}",
                                style = MaterialTheme.typography.bodySmall,
                                color = RenzoColors.MutedForeground,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.padding(start = 6.dp),
                            )
                        }
                        if (!credential.statusDetail.isNullOrBlank()) {
                            Text(
                                credential.statusDetail!!,
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.MutedForeground,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                    }
                    if (credential.supportsAutoLogin) {
                        if (pendingId == credential.id) {
                            CircularProgressIndicator(
                                color = RenzoColors.MutedForeground, strokeWidth = 2.dp,
                                modifier = Modifier.padding(end = 8.dp).size(16.dp),
                            )
                        } else {
                            IconGhostButton(
                                Icons.Filled.Refresh, "Test / re-login now", RenzoColors.Foreground,
                            ) {
                                pendingId = credential.id
                                scope.launch {
                                    val api = app.network.currentServiceOf<SiteAuthApi>()
                                    runCatching { api?.relogin(credential.id) }
                                        .onSuccess { it?.let { r -> report(r.result) } }
                                        .onFailure { snackbar.showSnackbar(it.apiMessage("Re-login failed")) }
                                    pendingId = null
                                    refresh()
                                }
                            }
                        }
                    }
                    IconGhostButton(Icons.Filled.Delete, "Remove login", RenzoColors.Red) {
                        confirmDelete = credential
                    }
                }
            }
        }

        // ── Add a login ─────────────────────────────────────────────────
        if (availableSites.isNotEmpty()) {
            CardDivider()
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(bottom = 12.dp)) {
                Icon(
                    Icons.Filled.VpnKey, contentDescription = null,
                    tint = RenzoColors.MutedForeground, modifier = Modifier.size(16.dp),
                )
                Text(
                    "Add a site login",
                    style = MaterialTheme.typography.titleSmall,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }

            FieldLabel("Site")
            RenzoSelect(
                options = availableSites.map { site ->
                    site.provider to buildString {
                        append(site.provider)
                        if (site.domain.isNotBlank()) append(" · ${site.domain}")
                        if (site.coin) append("  PAID")
                    }
                },
                value = provider,
                onChange = { provider = it },
                placeholder = "Choose a site",
            )
            Spacer(Modifier.height(12.dp))

            FieldLabel("Method")
            RenzoSelect(
                options = listOf(
                    "password" to "Username & password (auto-login)",
                    "cookie" to "Paste session cookie",
                ),
                value = mode,
                onChange = { mode = it },
            )
            Spacer(Modifier.height(12.dp))

            if (mode == "password") {
                LabelledField("Username / email", username, { username = it })
                LabelledField("Password", password, { password = it }, password = true)
            } else {
                LabelledField(
                    label = "Session cookie",
                    value = cookie,
                    onValueChange = { cookie = it },
                    placeholder = "name=value; name2=value2",
                    hint = "In your browser, log in to the site, open DevTools → Application → " +
                        "Cookies, and paste the cookie string (or the specific session cookie).",
                )
            }

            val canSubmit = provider.isNotEmpty() &&
                if (mode == "password") username.isNotBlank() && password.isNotEmpty() else cookie.isNotBlank()

            if (canSubmit) {
                RenzoButton(
                    text = if (mode == "password") "Log in & save" else "Save cookie",
                    icon = Icons.Filled.VpnKey,
                    busy = busy,
                    onClick = {
                        busy = true
                        scope.launch {
                            val api = app.network.currentServiceOf<SiteAuthApi>()
                            runCatching {
                                if (mode == "password") {
                                    api?.save(SaveSiteLoginDto(provider, username.trim(), password))
                                } else {
                                    api?.saveCookie(SaveSiteCookieDto(provider, username.trim(), cookie.trim()))
                                }
                            }
                                .onSuccess { it?.let { r -> report(r.result) } }
                                .onFailure { snackbar.showSnackbar(it.apiMessage("Failed to save login")) }
                            username = ""; password = ""; cookie = ""
                            busy = false
                            refresh()
                        }
                    },
                )
            }
        } else if (!loading && creds.isNotEmpty()) {
            CardDivider()
            Text(
                "All known coin sites are configured. Remove one above to switch accounts.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
            )
        }
    }

    confirmDelete?.let { target ->
        RenzoDialog(
            onDismiss = { confirmDelete = null },
            title = "Remove login",
            description = "Remove the saved login for ${target.provider}? Locked chapters will stop loading.",
        ) {
            Row(modifier = Modifier.fillMaxWidth()) {
                Box(modifier = Modifier.weight(1f))
                RenzoButton("Cancel", variant = "outline", onClick = { confirmDelete = null })
                Spacer(Modifier.width(8.dp))
                RenzoButton(
                    text = "Remove",
                    variant = "destructive",
                    onClick = {
                        confirmDelete = null
                        scope.launch {
                            val api = app.network.currentServiceOf<SiteAuthApi>()
                            runCatching { api?.remove(target.id) }
                                .onFailure { snackbar.showSnackbar(it.apiMessage("Failed to remove login")) }
                            refresh()
                        }
                    },
                )
            }
        }
    }
}

/** The web's StatusIcon: ok/manual_cookie ✓, failed ⚠, anything else ⏱. */
@Composable
private fun StatusIcon(status: String) {
    val (icon, tint) = when (status) {
        "ok" -> Icons.Filled.Check to RenzoColors.Emerald
        "manual_cookie" -> Icons.Filled.Check to RenzoColors.Blue
        "failed" -> Icons.Filled.Warning to RenzoColors.Red
        else -> Icons.Filled.Schedule to RenzoColors.Amber
    }
    Icon(icon, contentDescription = status, tint = tint, modifier = Modifier.size(16.dp))
}
