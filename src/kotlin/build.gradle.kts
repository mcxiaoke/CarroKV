plugins {
    `java-library`
    kotlin("jvm") version "2.4.21"
    `maven-publish`
}

group = "com.github.mcxiaoke"
version = project.findProperty("version")?.toString()?.takeIf { it != "unspecified" }
    ?: (System.getenv("VERSION") ?: "0.2.0")

repositories {
    mavenCentral()
}

dependencies {
    api(kotlin("stdlib"))
    testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
    testLogging {
        events("passed", "skipped", "failed")
    }
}

kotlin {
    jvmToolchain(17)
}

java {
    withSourcesJar()
}

publishing {
    publications {
        create<MavenPublication>("maven") {
            from(components["java"])
            artifactId = "CarroKV"

            pom {
                name.set("CarroKV")
                description.set("Cross-platform, strictly deterministic embedded Key-Value database conforming to KV-DB-DESIGN-SPEC v3.2.")
                url.set("https://github.com/mcxiaoke/CarroKV")
                licenses {
                    license {
                        name.set("The Apache License, Version 2.0")
                        url.set("http://www.apache.org/licenses/LICENSE-2.0.txt")
                    }
                }
                developers {
                    developer {
                        id.set("mcxiaoke")
                        name.set("mcxiaoke")
                    }
                }
                scm {
                    connection.set("scm:git:git://github.com/mcxiaoke/CarroKV.git")
                    developerConnection.set("scm:git:ssh://github.com/mcxiaoke/CarroKV.git")
                    url.set("https://github.com/mcxiaoke/CarroKV")
                }
            }
        }
    }
}
