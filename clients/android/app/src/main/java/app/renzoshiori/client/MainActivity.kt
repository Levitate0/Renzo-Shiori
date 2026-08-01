package app.renzoshiori.client

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
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
        setContent {
            RenzoTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
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
