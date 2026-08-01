package app.renzoshiori.client

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import app.renzoshiori.client.ui.auth.AuthStep
import app.renzoshiori.client.ui.auth.AuthViewModel
import app.renzoshiori.client.ui.auth.ConnectScreen
import app.renzoshiori.client.ui.auth.LoginScreen
import app.renzoshiori.client.ui.home.HomeShell
import app.renzoshiori.client.ui.reader.ReaderScreen
import app.renzoshiori.client.ui.series.OfflineSeriesScreen
import app.renzoshiori.client.ui.series.SeriesDetailScreen
import app.renzoshiori.client.ui.settings.AccountScreen
import app.renzoshiori.client.ui.theme.RenzoTheme

/**
 * Native Compose rewrite — replaces the old WebView shell entirely (no more
 * window.__RenzoAndroid JS bridge; this app talks to RenzoBackend's REST API
 * directly). Auth gate (Connect → Login) wraps a NavHost with the signed-in
 * graph: Library → Series/OfflineSeries → Reader, plus Account.
 */
class MainActivity : ComponentActivity() {
    private val authViewModel: AuthViewModel by viewModels { AuthViewModel.factory(application) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val crashFile = java.io.File(filesDir, "last-crash.txt")
        setContent {
            RenzoTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    var crashText by androidx.compose.runtime.remember {
                        androidx.compose.runtime.mutableStateOf(
                            if (crashFile.exists()) runCatching { crashFile.readText() }.getOrNull() else null,
                        )
                    }
                    if (crashText != null) {
                        CrashReportScreen(
                            trace = crashText!!,
                            onDismiss = { crashFile.delete(); crashText = null },
                        )
                        return@Surface
                    }
                    val state by authViewModel.state.collectAsState()
                    when (val step = state.step) {
                        is AuthStep.Connect -> ConnectScreen(
                            loading = state.loading,
                            error = state.error,
                            onConnect = authViewModel::connect,
                        )
                        is AuthStep.Login -> LoginScreen(
                            step = step,
                            loading = state.loading,
                            error = state.error,
                            onLogin = authViewModel::login,
                            onSelectUser = authViewModel::selectUser,
                        )
                        is AuthStep.SignedIn -> SignedInNavHost(
                            user = step.user,
                            onLogout = authViewModel::logout,
                        )
                    }
                }
            }
        }
    }
}

/**
 * Shown once after a crash: the saved stack trace, copyable, so crashes can
 * be reported from the device itself. Dismiss deletes the record and boots
 * the app normally.
 */
@Composable
private fun CrashReportScreen(trace: String, onDismiss: () -> Unit) {
    val clipboard = LocalClipboardManager.current
    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Text("The app crashed last time", style = MaterialTheme.typography.titleMedium)
        Text(
            trace,
            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
                .padding(vertical = 12.dp)
                .verticalScroll(rememberScrollState()),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(onClick = { clipboard.setText(AnnotatedString(trace)) }) {
                Text("Copy")
            }
            Button(onClick = onDismiss) {
                Text("Continue")
            }
        }
    }
}

@androidx.compose.runtime.Composable
private fun SignedInNavHost(user: app.renzoshiori.client.data.model.UserDto, onLogout: () -> Unit) {
    val nav = rememberNavController()

    NavHost(navController = nav, startDestination = "home") {
        composable("home") {
            HomeShell(
                user = user,
                onOpenSeries = { id -> nav.navigate("series/$id") },
                onOpenOfflineSeries = { id -> nav.navigate("offline-series/$id") },
                onOpenAccount = { nav.navigate("account") },
                onLogout = onLogout,
            )
        }
        composable(
            "series/{seriesId}",
            arguments = listOf(navArgument("seriesId") { type = NavType.StringType }),
        ) { entry ->
            val seriesId = entry.arguments!!.getString("seriesId")!!
            SeriesDetailScreen(
                seriesId = seriesId,
                onBack = { nav.popBackStack() },
                onReadChapter = { sid, ch -> nav.navigate("reader/$sid/$ch") },
            )
        }
        composable(
            "offline-series/{seriesId}",
            arguments = listOf(navArgument("seriesId") { type = NavType.StringType }),
        ) { entry ->
            val seriesId = entry.arguments!!.getString("seriesId")!!
            OfflineSeriesScreen(
                seriesId = seriesId,
                onBack = { nav.popBackStack() },
                onReadChapter = { sid, ch -> nav.navigate("reader/$sid/$ch") },
            )
        }
        composable(
            "reader/{seriesId}/{chapter}",
            arguments = listOf(
                navArgument("seriesId") { type = NavType.StringType },
                navArgument("chapter") { type = NavType.FloatType },
            ),
        ) { entry ->
            val seriesId = entry.arguments!!.getString("seriesId")!!
            val chapter = entry.arguments!!.getFloat("chapter").toDouble()
            ReaderScreen(
                seriesId = seriesId,
                chapterNumber = chapter,
                onExit = { nav.popBackStack() },
            )
        }
        composable("account") {
            AccountScreen(
                username = user.username,
                onBack = { nav.popBackStack() },
                onLogout = onLogout,
            )
        }
    }
}
