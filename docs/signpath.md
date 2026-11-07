# Signature de l'exécutable avec SignPath Foundation

AV1 Encod est éligible à la signature gratuite de [SignPath Foundation](https://signpath.org) pour les projets open source
(licence OSI : GPL-3.0, dépôt public, exe construit par GitHub Actions). Le certificat est au nom de **SignPath Foundation**.

Le workflow [`release.yml`](../.github/workflows/release.yml) est déjà prêt : la signature s'active toute seule dès que le
secret `SIGNPATH_API_TOKEN` existe. Sans lui, les versions sont publiées non signées comme avant.

## 1. Avant de postuler

- Activer la **double authentification** sur GitHub : <https://github.com/settings/security> (obligatoire pour tous les membres de l'équipe).
- Vérifier que le README contient bien la section [Code signing policy](../README.md#code-signing-policy) (attribution, rôles, confidentialité).

## 2. Postuler

Formulaire : <https://signpath.org/apply>. Informations utiles :

| Champ | Valeur |
|---|---|
| Projet | AV1 Encod |
| Dépôt | https://github.com/Araisite/AV1Encod |
| Licence | GPL-3.0-or-later |
| Page de téléchargement | https://github.com/Araisite/AV1Encod/releases |
| Description | Interface Windows (WPF/.NET) pour réencoder films et séries en AV1 avec HandBrakeCLI |
| Système de build | GitHub Actions (runners hébergés par GitHub) |
| Fichier signé | `AV1Encod.exe` (exécutable .NET autonome, un seul fichier) |

L'acceptation est à leur discrétion : pour un exécutable, ils demandent une certaine réputation vérifiable du projet
(utilisateurs, étoiles, activité). Un projet très récent peut être refusé ou mis en attente.

## 3. Après l'acceptation (sur signpath.io)

1. Activer la double authentification sur le compte SignPath.
2. Dans l'organisation : ajouter le *Trusted Build System* **GitHub.com** et le lier au projet ; installer l'application
   GitHub SignPath sur le dépôt `Araisite/AV1Encod`.
3. Projet `AV1Encod` → **Artifact configuration** : l'exe est envoyé par `actions/upload-artifact`, donc dans un ZIP.
   Base de configuration (à compléter avec les restrictions de métadonnées exigées par SignPath :
   *product name* = `AV1 Encod`, *product version* identique pour tous les fichiers d'une version) :

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="AV1Encod.exe">
         <authenticode-sign/>
       </pe-file>
     </zip-file>
   </artifact-configuration>
   ```

4. **Signing policy** `release-signing` (certificat de SignPath Foundation), avec **Araisite** comme *approver*.
5. Créer un utilisateur CI avec le rôle *submitter* et générer son **API token**.

## 4. Brancher le dépôt GitHub

Dépôt → *Settings* → *Secrets and variables* → *Actions* :

| Type | Nom | Valeur |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | le token de l'étape 3.5 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | l'identifiant de l'organisation SignPath |
| Variable (si différent) | `SIGNPATH_PROJECT_SLUG` | défaut : `AV1Encod` |
| Variable (si différent) | `SIGNPATH_POLICY_SLUG` | défaut : `release-signing` |
| Variable (si différent) | `SIGNPATH_ARTIFACT_CONFIG_SLUG` | défaut : `initial` |

## 5. Publier une version signée

```powershell
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions construit l'exe et l'envoie à SignPath. **Approuvez la demande sur signpath.io dans l'heure** (vous recevez un
e-mail) ; la Release est ensuite créée avec l'exe signé et la mention d'attribution.

À partir de la première version signée, l'application **refuse toute mise à jour qui ne serait pas signée par le même
éditeur** (vérification Authenticode avant de remplacer l'exe).
